using ZestResourceOptimizer.Models;

namespace ZestResourceOptimizer.Services;

internal sealed class PdhCpuSampler : IDisposable
{
    private const uint ErrorSuccess = 0;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtDouble = 0x00000200;
    private readonly int _processorCount = Environment.ProcessorCount;
    private IntPtr _query;
    private IntPtr _counter;
    private bool _available;
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private bool _hasSystemTimesSample;
    private IReadOnlyList<ProcessorPowerInformation> _cachedPowerInfo = [];
    private DateTime _lastPowerInfoRead = DateTime.MinValue;

    public PdhCpuSampler()
    {
        InitializePdh();
        _ = TryReadTotalFromSystemTimes();
    }

    public (double totalCpu, IReadOnlyList<CoreMetric> cores) Read()
    {
        var powerInfo = ReadPowerInfo();
        if (_available)
        {
            var pdhCores = ReadFromPdh(powerInfo);
            if (pdhCores.Count > 0)
            {
                var total = pdhCores.FirstOrDefault(c => c.Index == -1)?.UsagePercent
                    ?? pdhCores.Where(c => c.Index >= 0).DefaultIfEmpty().Average(c => c?.UsagePercent ?? 0);
                return (ClampPercent(total), pdhCores.Where(c => c.Index >= 0).OrderBy(c => c.Index).ToList());
            }
        }

        var fallbackTotal = TryReadTotalFromSystemTimes();
        var fallbackCores = Enumerable.Range(0, _processorCount)
            .Select(index => CreateCoreMetric(index, fallbackTotal, powerInfo))
            .ToList();
        return (fallbackTotal, fallbackCores);
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            _ = NativeMethods.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    private void InitializePdh()
    {
        if (NativeMethods.PdhOpenQuery(null, UIntPtr.Zero, out _query) != ErrorSuccess)
        {
            return;
        }

        var result = NativeMethods.PdhAddEnglishCounter(_query, @"\Processor(*)\% Processor Time", UIntPtr.Zero, out _counter);
        if (result != ErrorSuccess)
        {
            _ = NativeMethods.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            return;
        }

        _available = NativeMethods.PdhCollectQueryData(_query) == ErrorSuccess;
    }

    private List<CoreMetric> ReadFromPdh(IReadOnlyList<ProcessorPowerInformation> powerInfo)
    {
        if (NativeMethods.PdhCollectQueryData(_query) != ErrorSuccess)
        {
            return [];
        }

        uint bufferSize = 0;
        uint itemCount = 0;
        var result = NativeMethods.PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref bufferSize, ref itemCount, IntPtr.Zero);
        if (result != PdhMoreData || bufferSize == 0 || itemCount == 0)
        {
            return [];
        }

        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            result = NativeMethods.PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref bufferSize, ref itemCount, buffer);
            if (result != ErrorSuccess)
            {
                return [];
            }

            var itemSize = System.Runtime.InteropServices.Marshal.SizeOf<PdhCounterValueItem>();
            var cores = new List<CoreMetric>();
            for (var i = 0; i < itemCount; i++)
            {
                var item = System.Runtime.InteropServices.Marshal.PtrToStructure<PdhCounterValueItem>(IntPtr.Add(buffer, i * itemSize));
                var name = System.Runtime.InteropServices.Marshal.PtrToStringUni(item.Name) ?? string.Empty;
                var index = name.Equals("_Total", StringComparison.OrdinalIgnoreCase) ? -1 : ParseCoreIndex(name);
                if (index < -1)
                {
                    continue;
                }

                cores.Add(CreateCoreMetric(index, item.Value.DoubleValue, powerInfo));
            }

            return cores;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    private IReadOnlyList<ProcessorPowerInformation> ReadPowerInfo()
    {
        if ((DateTime.UtcNow - _lastPowerInfoRead) < TimeSpan.FromSeconds(5))
        {
            return _cachedPowerInfo;
        }

        _cachedPowerInfo = NativeMethods.GetProcessorPowerInformation(_processorCount);
        _lastPowerInfoRead = DateTime.UtcNow;
        return _cachedPowerInfo;
    }

    private double TryReadTotalFromSystemTimes()
    {
        if (!NativeMethods.GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return 0;
        }

        var idle = idleTime.ToUInt64();
        var kernel = kernelTime.ToUInt64();
        var user = userTime.ToUInt64();

        if (!_hasSystemTimesSample)
        {
            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
            _hasSystemTimesSample = true;
            return 0;
        }

        var idleDelta = idle - _lastIdle;
        var kernelDelta = kernel - _lastKernel;
        var userDelta = user - _lastUser;
        var total = kernelDelta + userDelta;

        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;

        if (total == 0)
        {
            return 0;
        }

        return ClampPercent((total - idleDelta) * 100.0 / total);
    }

    private static CoreMetric CreateCoreMetric(int index, double usage, IReadOnlyList<ProcessorPowerInformation> powerInfo)
    {
        var processorPower = powerInfo.FirstOrDefault(p => p.Number == index);
        var currentMhz = (int)processorPower.CurrentMhz;
        var maxMhz = (int)processorPower.MaxMhz;
        var state = index < 0
            ? "CoreStateTotal"
            : currentMhz <= 0 && maxMhz > 0
                ? "CoreStateParked"
                : "CoreStateActive";

        return new CoreMetric
        {
            Index = index,
            UsagePercent = ClampPercent(usage),
            CurrentMhz = currentMhz,
            MaxMhz = maxMhz,
            ParkingState = state
        };
    }

    private static int ParseCoreIndex(string name)
    {
        return int.TryParse(name, out var value) ? value : -2;
    }

    private static double ClampPercent(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Math.Clamp(value, 0, 100);
    }
}
