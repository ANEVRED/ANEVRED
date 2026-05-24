namespace ZestResourceOptimizer.Models;

public sealed class SystemSnapshot
{
    public double RamUsagePercent { get; init; }
    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }
    public double PagefileUsagePercent { get; init; }
    public double PagefileUsedGb { get; init; }
    public double PagefileTotalGb { get; init; }
    public double CpuUsagePercent { get; init; }
    public double GpuUsagePercent { get; init; }
    public double VramUsagePercent { get; init; }
    public double VramUsedGb { get; init; }
    public double VramTotalGb { get; init; }
    public bool IsGpuDataAvailable { get; init; }
    public double AverageFrametimeMs { get; init; }
    public double TemperatureCelsius { get; init; }
    public double CpuTemperatureCelsius { get; init; }
    public IReadOnlyList<CoreMetric> Cores { get; init; } = [];
    public IReadOnlyList<ProcessSnapshot> Processes { get; init; } = [];
    public bool ProcessDataUpdated { get; init; }
}
