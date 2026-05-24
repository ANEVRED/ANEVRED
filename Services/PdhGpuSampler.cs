using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using ZestResourceOptimizer.Models;

namespace ZestResourceOptimizer.Services;

internal sealed class PdhGpuSampler : IDisposable
{
    private const uint ErrorSuccess = 0;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtDouble = 0x00000200;

    private IntPtr _query;
    private IntPtr _engineCounter;
    private IntPtr _dedicatedUsageCounter;
    private IntPtr _processDedicatedUsageCounter;
    private IntPtr _dedicatedLimitCounter;
    private bool _hasAnyCounter;
    private GpuMetric? _lastValidMetric;
    private IReadOnlyDictionary<int, ProcessGpuMetric> _lastProcessMetrics = new Dictionary<int, ProcessGpuMetric>();

    public IReadOnlyDictionary<int, ProcessGpuMetric> LastProcessMetrics => _lastProcessMetrics;

    public PdhGpuSampler()
    {
        Initialize();
    }

    public GpuMetric Read()
    {
        var nvidiaMetric = TryReadNvidiaSmi();
        if (nvidiaMetric is not null)
        {
            RefreshProcessMetricsFromPdh();
            return RememberIfValid(nvidiaMetric);
        }

        if (!_hasAnyCounter || _query == IntPtr.Zero)
        {
            return _lastValidMetric ?? new GpuMetric();
        }

        if (NativeMethods.PdhCollectQueryData(_query) != ErrorSuccess)
        {
            return _lastValidMetric ?? new GpuMetric();
        }

        var engineValues = _engineCounter == IntPtr.Zero
            ? []
            : ReadCounterValues(_engineCounter);
        var dedicatedUsageValues = _dedicatedUsageCounter == IntPtr.Zero
            ? []
            : ReadCounterValues(_dedicatedUsageCounter);
        var processDedicatedUsageValues = _processDedicatedUsageCounter == IntPtr.Zero
            ? dedicatedUsageValues
            : ReadCounterValues(_processDedicatedUsageCounter);

        var gpuUsage = _engineCounter == IntPtr.Zero
            ? 0
            : engineValues.Where(item => Is3DEngine(item.Name)).Sum(item => item.Value);

        var dedicatedBytes = _dedicatedUsageCounter == IntPtr.Zero
            ? 0
            : dedicatedUsageValues.Sum(item => item.Value);

        var limitBytes = _dedicatedLimitCounter == IntPtr.Zero
            ? 0
            : ReadCounterValues(_dedicatedLimitCounter).Sum(item => item.Value);

        _lastProcessMetrics = BuildProcessMetrics(engineValues, processDedicatedUsageValues);

        return RememberIfValid(new GpuMetric
        {
            UsagePercent = ClampPercent(gpuUsage),
            VramUsedGb = BytesToGb(dedicatedBytes),
            VramTotalGb = BytesToGb(limitBytes),
            IsAvailable = IsFinite(gpuUsage) || dedicatedBytes > 0 || limitBytes > 0
        });
    }

    private void RefreshProcessMetricsFromPdh()
    {
        if (!_hasAnyCounter || _query == IntPtr.Zero)
        {
            return;
        }

        if (NativeMethods.PdhCollectQueryData(_query) != ErrorSuccess)
        {
            return;
        }

        var engineValues = _engineCounter == IntPtr.Zero
            ? []
            : ReadCounterValues(_engineCounter);
        var dedicatedUsageValues = _dedicatedUsageCounter == IntPtr.Zero
            ? []
            : ReadCounterValues(_dedicatedUsageCounter);
        var processDedicatedUsageValues = _processDedicatedUsageCounter == IntPtr.Zero
            ? dedicatedUsageValues
            : ReadCounterValues(_processDedicatedUsageCounter);

        _lastProcessMetrics = BuildProcessMetrics(engineValues, processDedicatedUsageValues);
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            _ = NativeMethods.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    private void Initialize()
    {
        if (NativeMethods.PdhOpenQuery(null, UIntPtr.Zero, out _query) != ErrorSuccess)
        {
            return;
        }

        _engineCounter = TryAddCounter(@"\GPU Engine(*)\Utilization Percentage");
        _dedicatedUsageCounter = TryAddCounter(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _processDedicatedUsageCounter = TryAddCounter(@"\GPU Process Memory(*)\Dedicated Usage");
        _dedicatedLimitCounter = TryAddCounter(@"\GPU Adapter Memory(*)\Dedicated Limit");
        _hasAnyCounter = _engineCounter != IntPtr.Zero
            || _dedicatedUsageCounter != IntPtr.Zero
            || _processDedicatedUsageCounter != IntPtr.Zero
            || _dedicatedLimitCounter != IntPtr.Zero;

        if (_hasAnyCounter)
        {
            _ = NativeMethods.PdhCollectQueryData(_query);
        }
    }

    private IntPtr TryAddCounter(string path)
    {
        return NativeMethods.PdhAddEnglishCounter(_query, path, UIntPtr.Zero, out var counter) == ErrorSuccess
            ? counter
            : IntPtr.Zero;
    }

    private static IReadOnlyList<(string Name, double Value)> ReadCounterValues(IntPtr counter)
    {
        uint bufferSize = 0;
        uint itemCount = 0;
        var result = NativeMethods.PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref bufferSize, ref itemCount, IntPtr.Zero);
        if (result != PdhMoreData || bufferSize == 0 || itemCount == 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            result = NativeMethods.PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref bufferSize, ref itemCount, buffer);
            if (result != ErrorSuccess)
            {
                return [];
            }

            var values = new List<(string Name, double Value)>((int)itemCount);
            var itemSize = Marshal.SizeOf<PdhCounterValueItem>();
            for (var i = 0; i < itemCount; i++)
            {
                var item = Marshal.PtrToStructure<PdhCounterValueItem>(IntPtr.Add(buffer, i * itemSize));
                var name = Marshal.PtrToStringUni(item.Name) ?? string.Empty;
                if (item.Value.CStatus == ErrorSuccess && IsFinite(item.Value.DoubleValue))
                {
                    values.Add((name, item.Value.DoubleValue));
                }
            }

            return values;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool Is3DEngine(string instanceName)
    {
        return instanceName.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<int, ProcessGpuMetric> BuildProcessMetrics(
        IReadOnlyList<(string Name, double Value)> engineValues,
        IReadOnlyList<(string Name, double Value)> dedicatedUsageValues)
    {
        var metrics = new Dictionary<int, ProcessGpuMetric>();
        foreach (var item in engineValues)
        {
            if (!Is3DEngine(item.Name) || !TryReadPid(item.Name, out var pid))
            {
                continue;
            }

            var current = metrics.TryGetValue(pid, out var metric) ? metric : new ProcessGpuMetric();
            metrics[pid] = current with { GpuPercent = current.GpuPercent + ClampPercent(item.Value) };
        }

        foreach (var item in dedicatedUsageValues)
        {
            if (!TryReadPid(item.Name, out var pid))
            {
                continue;
            }

            var current = metrics.TryGetValue(pid, out var metric) ? metric : new ProcessGpuMetric();
            metrics[pid] = current with { VramMb = Math.Max(current.VramMb, BytesToMb(item.Value)) };
        }

        return metrics;
    }

    private static bool TryReadPid(string instanceName, out int pid)
    {
        pid = 0;
        var marker = "pid_";
        var index = instanceName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        index += marker.Length;
        var end = index;
        while (end < instanceName.Length && char.IsDigit(instanceName[end]))
        {
            end++;
        }

        return end > index && int.TryParse(instanceName[index..end], out pid);
    }

    private static double BytesToGb(double bytes)
    {
        return !IsFinite(bytes) || bytes <= 0 ? 0 : bytes / 1024d / 1024d / 1024d;
    }

    private static double BytesToMb(double bytes)
    {
        return !IsFinite(bytes) || bytes <= 0 ? 0 : bytes / 1024d / 1024d;
    }

    private GpuMetric RememberIfValid(GpuMetric metric)
    {
        if (metric.IsAvailable && (metric.HasValidVram || IsFinite(metric.UsagePercent)))
        {
            _lastValidMetric = metric;
        }

        return metric.IsAvailable ? metric : _lastValidMetric ?? metric;
    }

    private static GpuMetric? TryReadNvidiaSmi()
    {
        var exe = FindNvidiaSmi();
        if (exe is null)
        {
            return null;
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();
            if (!process.WaitForExit(1500))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort cleanup only.
                }

                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseNvidiaLine)
                .Where(metric => metric is not null)
                .Select(metric => metric!)
                .OrderByDescending(metric => metric.VramUsedGb)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static GpuMetric? ParseNvidiaLine(string line)
    {
        var parts = line.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 5)
        {
            return null;
        }

        if (!TryParseDouble(parts[1], out var usage)
            || !TryParseDouble(parts[2], out var usedMb)
            || !TryParseDouble(parts[3], out var totalMb))
        {
            return null;
        }

        _ = TryParseDouble(parts[4], out var temperature);

        return new GpuMetric
        {
            UsagePercent = ClampPercent(usage),
            VramUsedGb = usedMb / 1024d,
            VramTotalGb = totalMb / 1024d,
            TemperatureCelsius = IsFinite(temperature) ? temperature : 0,
            IsAvailable = IsFinite(totalMb) && totalMb > 0
        };
    }

    private static bool TryParseDouble(string value, out double result)
    {
        var parsed = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result);
        return parsed && IsFinite(result);
    }

    private static double ClampPercent(double value)
    {
        return IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static string? FindNvidiaSmi()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "nvidia-smi.exe")
        };

        var pathCandidate = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path, "nvidia-smi.exe"))
            .FirstOrDefault(File.Exists);

        return candidates.FirstOrDefault(File.Exists) ?? pathCandidate;
    }
}
