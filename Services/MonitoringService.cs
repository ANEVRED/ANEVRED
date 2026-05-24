using System.Diagnostics;
using ZestResourceOptimizer.Models;

namespace ZestResourceOptimizer.Services;

public sealed class MonitoringService : IDisposable
{
    private readonly ProcessProtectionService _protectionService;
    private readonly PdhCpuSampler _cpuSampler = new();
    private readonly PdhGpuSampler _gpuSampler = new();
    private readonly CpuTemperatureSampler _cpuTemperatureSampler = new();
    private readonly Dictionary<int, (TimeSpan CpuTime, DateTime SampleTime)> _processCpuSamples = [];
    private readonly int _processorCount = Environment.ProcessorCount;

    public MonitoringService(ProcessProtectionService protectionService)
    {
        _protectionService = protectionService;
    }

    public SystemSnapshot GetSnapshot(bool includeProcesses = true, int maxProcesses = 150)
    {
        var memory = MemoryStatusEx.Create();
        if (!NativeMethods.GlobalMemoryStatusEx(ref memory))
        {
            NativeMethods.ThrowLastWin32Error("GlobalMemoryStatusEx");
        }

        var totalGb = BytesToGb(memory.ullTotalPhys);
        var availableGb = BytesToGb(memory.ullAvailPhys);
        var usedGb = Math.Max(0, totalGb - availableGb);
        var pagefileTotalGb = BytesToGb(memory.ullTotalPageFile);
        var pagefileAvailableGb = BytesToGb(memory.ullAvailPageFile);
        var pagefileUsedGb = Math.Max(0, pagefileTotalGb - pagefileAvailableGb);
        var (totalCpu, cores) = _cpuSampler.Read();
        var gpu = _gpuSampler.Read();
        var cpuTemperature = _cpuTemperatureSampler.Read();
        var processes = includeProcesses ? ReadProcesses(maxProcesses, _gpuSampler.LastProcessMetrics) : [];
        var gpuAvailable = gpu.IsAvailable && IsFinite(gpu.UsagePercent);
        var vramAvailable = gpu.HasValidVram;

        return new SystemSnapshot
        {
            RamUsagePercent = memory.dwMemoryLoad,
            RamUsedGb = usedGb,
            RamTotalGb = totalGb,
            PagefileUsagePercent = pagefileTotalGb > 0 ? Math.Clamp(pagefileUsedGb / pagefileTotalGb * 100, 0, 100) : 0,
            PagefileUsedGb = pagefileUsedGb,
            PagefileTotalGb = pagefileTotalGb,
            CpuUsagePercent = totalCpu,
            GpuUsagePercent = SanitizePercent(gpu.UsagePercent),
            VramUsagePercent = vramAvailable ? SanitizePercent(gpu.VramUsagePercent) : 0,
            VramUsedGb = vramAvailable ? gpu.VramUsedGb : 0,
            VramTotalGb = vramAvailable ? gpu.VramTotalGb : 0,
            IsGpuDataAvailable = gpuAvailable || vramAvailable,
            AverageFrametimeMs = EstimateFrametime(totalCpu, gpu.UsagePercent),
            TemperatureCelsius = IsFinite(gpu.TemperatureCelsius) ? gpu.TemperatureCelsius : 0,
            CpuTemperatureCelsius = IsFinite(cpuTemperature) ? cpuTemperature : 0,
            Cores = cores,
            Processes = processes,
            ProcessDataUpdated = includeProcesses
        };
    }

    public void Dispose()
    {
        _cpuSampler.Dispose();
        _gpuSampler.Dispose();
    }

    private List<ProcessSnapshot> ReadProcesses(int maxProcesses, IReadOnlyDictionary<int, ProcessGpuMetric> processGpuMetrics)
    {
        var now = DateTime.UtcNow;
        var metrics = new List<ProcessMetric>();
        var seen = new HashSet<int>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var id = process.Id;
                    seen.Add(id);
                    var name = process.ProcessName;
                    var cpuTime = process.TotalProcessorTime;
                    var cpuPercent = CalculateProcessCpuPercent(id, cpuTime, now);
                    var (memoryMb, commitMb) = ReadProcessMemory(process);
                    var gpuMetric = processGpuMetrics.TryGetValue(id, out var processGpuMetric)
                        ? processGpuMetric
                        : new ProcessGpuMetric();
                    var protectedByRule = _protectionService.IsProtectedProcessName(name);
                    var critical = _protectionService.IsCriticalProcessName(name);

                    metrics.Add(new ProcessMetric(
                        id,
                        name,
                        cpuPercent,
                        gpuMetric.GpuPercent,
                        memoryMb,
                        commitMb,
                        gpuMetric.VramMb,
                        protectedByRule,
                        critical,
                        _protectionService.IsStarCitizen(name)));
                }
                catch
                {
                    // Processes can exit or deny access while the snapshot is being built.
                }
            }
        }

        foreach (var stalePid in _processCpuSamples.Keys.Where(pid => !seen.Contains(pid)).ToList())
        {
            _processCpuSamples.Remove(stalePid);
        }

        var limit = Math.Clamp(maxProcesses, 50, 300);
        var halfLimit = Math.Max(25, limit / 2);
        var selectedMetrics = metrics
            .OrderByDescending(process => process.CpuPercent)
            .Take(halfLimit)
            .Concat(metrics.OrderByDescending(process => process.MemoryMb).Take(halfLimit))
            .Concat(metrics.OrderByDescending(process => process.CommitMb).Take(halfLimit))
            .Concat(metrics.OrderByDescending(process => process.VramMb).Take(halfLimit))
            .DistinctBy(process => process.Id)
            .OrderByDescending(process => process.CpuPercent)
            .ThenByDescending(process => process.GpuPercent)
            .ThenByDescending(process => process.CommitMb)
            .ThenByDescending(process => process.MemoryMb)
            .ThenByDescending(process => process.VramMb)
            .Take(limit)
            .ToList();

        var snapshots = new List<ProcessSnapshot>(selectedMetrics.Count);
        foreach (var metric in selectedMetrics)
        {
            var (priority, title, executablePath) = ReadProcessDetails(metric.Id);
            var isBackground = string.IsNullOrWhiteSpace(title);
            snapshots.Add(new ProcessSnapshot
            {
                Id = metric.Id,
                Name = metric.Name,
                ExecutableName = metric.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? metric.Name : metric.Name + ".exe",
                ExecutablePath = executablePath,
                CpuPercent = metric.CpuPercent,
                GpuPercent = metric.GpuPercent,
                MemoryMb = metric.MemoryMb,
                CommitMb = metric.CommitMb,
                VramMb = metric.VramMb,
                Priority = priority,
                WindowTitle = title,
                IsProtected = metric.IsProtected,
                IsCritical = metric.IsCritical,
                IsStarCitizen = metric.IsStarCitizen,
                IsBackground = isBackground,
                Status = metric.IsCritical ? "StatusSystem" : metric.IsProtected ? "StatusProtected" : isBackground ? "StatusBackground" : "StatusForeground"
            });
        }

        return snapshots;
    }

    private readonly record struct ProcessMetric(
        int Id,
        string Name,
        double CpuPercent,
        double GpuPercent,
        double MemoryMb,
        double CommitMb,
        double VramMb,
        bool IsProtected,
        bool IsCritical,
        bool IsStarCitizen);

    private static (double WorkingSetMb, double PrivateCommitMb) ReadProcessMemory(Process process)
    {
        var workingSetMb = BytesToMb(process.WorkingSet64);
        var privateCommitMb = BytesToMb(process.PrivateMemorySize64);
        if (workingSetMb > 0 || privateCommitMb > 0)
        {
            return (workingSetMb, privateCommitMb);
        }

        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            return (workingSetMb, privateCommitMb);
        }

        try
        {
            var counters = new ProcessMemoryCountersEx { Cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ProcessMemoryCountersEx>() };
            if (!NativeMethods.GetProcessMemoryInfo(handle, out counters, counters.Cb))
            {
                return (workingSetMb, privateCommitMb);
            }

            return (BytesToMb((ulong)counters.WorkingSetSize), BytesToMb((ulong)counters.PrivateUsage));
        }
        finally
        {
            _ = NativeMethods.CloseHandle(handle);
        }
    }

    private static (string Priority, string WindowTitle, string ExecutablePath) ReadProcessDetails(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return (ReadPriority(process), ReadWindowTitle(process), ReadExecutablePath(process));
        }
        catch
        {
            return ("Unknown", string.Empty, string.Empty);
        }
    }

    private double CalculateProcessCpuPercent(int processId, TimeSpan cpuTime, DateTime now)
    {
        if (!_processCpuSamples.TryGetValue(processId, out var previous))
        {
            _processCpuSamples[processId] = (cpuTime, now);
            return 0;
        }

        _processCpuSamples[processId] = (cpuTime, now);
        var elapsedMs = (now - previous.SampleTime).TotalMilliseconds;
        if (elapsedMs <= 0)
        {
            return 0;
        }

        var cpuMs = (cpuTime - previous.CpuTime).TotalMilliseconds;
        return Math.Clamp(cpuMs / elapsedMs / _processorCount * 100, 0, 100);
    }

    private static string ReadPriority(Process process)
    {
        try
        {
            return process.PriorityClass.ToString();
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string ReadWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static double BytesToGb(ulong bytes)
    {
        return bytes / 1024d / 1024d / 1024d;
    }

    private static double BytesToMb(long bytes)
    {
        return bytes <= 0 ? 0 : bytes / 1024d / 1024d;
    }

    private static double BytesToMb(ulong bytes)
    {
        return bytes == 0 ? 0 : bytes / 1024d / 1024d;
    }

    private static double SanitizePercent(double value)
    {
        return IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static double EstimateFrametime(double cpuUsagePercent, double gpuUsagePercent)
    {
        cpuUsagePercent = SanitizePercent(cpuUsagePercent);
        gpuUsagePercent = SanitizePercent(gpuUsagePercent);
        var load = Math.Max(cpuUsagePercent, gpuUsagePercent);
        if (load <= 0)
        {
            return 0;
        }

        // Passive placeholder until ETW/PresentMon-style frametime capture is added.
        return Math.Clamp(8 + load * 0.12, 8, 33);
    }
}
