using ANEVRED.Models;

namespace ANEVRED.Services;

internal sealed class PdhCpuSampler : IDisposable
{
    private const uint ErrorSuccess = 0;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtDouble = 0x00000200;
    private readonly int _processorCount = Environment.ProcessorCount;
    private IntPtr _query;
    private IntPtr _utilityCounter;
    private IntPtr _processorTimeCounter;
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
            if (NativeMethods.PdhCollectQueryData(_query) == ErrorSuccess)
            {
                var utilityCores = ReadFromPdh(_utilityCounter, powerInfo, ParseProcessorInformationIndex);
                if (utilityCores.Count > 0)
                {
                    return BuildResult(utilityCores);
                }

                var processorTimeCores = ReadFromPdh(_processorTimeCounter, powerInfo, ParseProcessorIndex);
                if (processorTimeCores.Count > 0)
                {
                    return BuildResult(processorTimeCores);
                }
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
            _utilityCounter = IntPtr.Zero;
            _processorTimeCounter = IntPtr.Zero;
        }
    }

    private void InitializePdh()
    {
        if (NativeMethods.PdhOpenQuery(null, UIntPtr.Zero, out _query) != ErrorSuccess)
        {
            return;
        }

        _ = NativeMethods.PdhAddEnglishCounter(_query, @"\Processor Information(*)\% Processor Utility", UIntPtr.Zero, out _utilityCounter);
        _ = NativeMethods.PdhAddEnglishCounter(_query, @"\Processor(*)\% Processor Time", UIntPtr.Zero, out _processorTimeCounter);
        if (_utilityCounter == IntPtr.Zero && _processorTimeCounter == IntPtr.Zero)
        {
            _ = NativeMethods.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            return;
        }

        _available = NativeMethods.PdhCollectQueryData(_query) == ErrorSuccess;
    }

    private List<CoreMetric> ReadFromPdh(
        IntPtr counter,
        IReadOnlyList<ProcessorPowerInformation> powerInfo,
        Func<string, int> parseIndex)
    {
        if (counter == IntPtr.Zero)
        {
            return [];
        }

        uint bufferSize = 0;
        uint itemCount = 0;
        var result = NativeMethods.PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref bufferSize, ref itemCount, IntPtr.Zero);
        if (result != PdhMoreData || bufferSize == 0 || itemCount == 0)
        {
            return [];
        }

        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            result = NativeMethods.PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref bufferSize, ref itemCount, buffer);
            if (result != ErrorSuccess)
            {
                return [];
            }

            var itemSize = System.Runtime.InteropServices.Marshal.SizeOf<PdhCounterValueItem>();
            var cores = new List<CoreMetric>();
            for (var i = 0; i < itemCount; i++)
            {
                var item = System.Runtime.InteropServices.Marshal.PtrToStructure<PdhCounterValueItem>(IntPtr.Add(buffer, i * itemSize));
                if (item.Value.CStatus != ErrorSuccess)
                {
                    continue;
                }

                var name = System.Runtime.InteropServices.Marshal.PtrToStringUni(item.Name) ?? string.Empty;
                var index = parseIndex(name);
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

    private static (double totalCpu, IReadOnlyList<CoreMetric> cores) BuildResult(IReadOnlyList<CoreMetric> pdhCores)
    {
        var total = pdhCores.FirstOrDefault(c => c.Index == -1)?.UsagePercent
            ?? pdhCores.Where(c => c.Index >= 0).DefaultIfEmpty().Average(c => c?.UsagePercent ?? 0);
        return (ClampPercent(total), pdhCores.Where(c => c.Index >= 0).OrderBy(c => c.Index).ToList());
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

    private static int ParseProcessorIndex(string name)
    {
        if (name.Equals("_Total", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        return int.TryParse(name, out var value) ? value : -2;
    }

    private static int ParseProcessorInformationIndex(string name)
    {
        if (name.Equals("_Total", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        var parts = name.Split(',', 2);
        if (parts.Length != 2
            || parts[1].Equals("_Total", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[0], out var group)
            || !int.TryParse(parts[1], out var processor))
        {
            return -2;
        }

        return group * 64 + processor;
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
