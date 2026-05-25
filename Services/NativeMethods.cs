using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ANEVRED.Services;

internal static class NativeMethods
{
    public const uint ProcessSetQuota = 0x0100;
    public const uint ProcessQueryInformation = 0x0400;
    public const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("psapi.dll", SetLastError = true)]
    internal static extern bool EmptyWorkingSet(IntPtr processHandle);

    [DllImport("psapi.dll", SetLastError = true)]
    internal static extern bool GetProcessMemoryInfo(IntPtr processHandle, out ProcessMemoryCountersEx counters, uint size);

    [DllImport("PowrProf.dll", SetLastError = true)]
    private static extern uint CallNtPowerInformation(
        int informationLevel,
        IntPtr inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhOpenQuery(string? dataSource, UIntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, UIntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhGetFormattedCounterArray(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        ref uint itemCount,
        IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCloseQuery(IntPtr query);

    internal static IReadOnlyList<ProcessorPowerInformation> GetProcessorPowerInformation(int processorCount)
    {
        var size = Marshal.SizeOf<ProcessorPowerInformation>() * processorCount;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = CallNtPowerInformation(11, IntPtr.Zero, 0, buffer, (uint)size);
            if (result != 0)
            {
                return [];
            }

            var values = new List<ProcessorPowerInformation>(processorCount);
            var itemSize = Marshal.SizeOf<ProcessorPowerInformation>();
            for (var i = 0; i < processorCount; i++)
            {
                values.Add(Marshal.PtrToStructure<ProcessorPowerInformation>(IntPtr.Add(buffer, i * itemSize)));
            }

            return values;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void ThrowLastWin32Error(string operation)
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryStatusEx
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;

    public static MemoryStatusEx Create()
    {
        return new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct FileTime
{
    public uint LowDateTime;
    public uint HighDateTime;

    public ulong ToUInt64() => ((ulong)HighDateTime << 32) | LowDateTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessorPowerInformation
{
    public uint Number;
    public uint MaxMhz;
    public uint CurrentMhz;
    public uint MhzLimit;
    public uint MaxIdleState;
    public uint CurrentIdleState;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessMemoryCountersEx
{
    public uint Cb;
    public uint PageFaultCount;
    public nuint PeakWorkingSetSize;
    public nuint WorkingSetSize;
    public nuint QuotaPeakPagedPoolUsage;
    public nuint QuotaPagedPoolUsage;
    public nuint QuotaPeakNonPagedPoolUsage;
    public nuint QuotaNonPagedPoolUsage;
    public nuint PagefileUsage;
    public nuint PeakPagefileUsage;
    public nuint PrivateUsage;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PdhCounterValueItem
{
    public IntPtr Name;
    public PdhFormattedCounterValue Value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PdhFormattedCounterValue
{
    public uint CStatus;
    private uint Padding;
    public double DoubleValue;
}
