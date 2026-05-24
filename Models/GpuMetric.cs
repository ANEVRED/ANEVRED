namespace ZestResourceOptimizer.Models;

public sealed class GpuMetric
{
    public double UsagePercent { get; init; }
    public double VramUsedGb { get; init; }
    public double VramTotalGb { get; init; }
    public double TemperatureCelsius { get; init; }
    public bool IsAvailable { get; init; }

    public double VramUsagePercent =>
        !IsFinite(VramUsedGb) || !IsFinite(VramTotalGb) || VramTotalGb <= 0
            ? 0
            : Math.Clamp(VramUsedGb / VramTotalGb * 100, 0, 100);

    public bool HasValidVram =>
        IsAvailable
        && IsFinite(VramUsedGb)
        && IsFinite(VramTotalGb)
        && VramTotalGb > 0
        && VramUsedGb >= 0;

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}

public readonly record struct ProcessGpuMetric(double GpuPercent, double VramMb);
