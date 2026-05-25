namespace ANEVRED.Models;

public sealed class CoreMetric
{
    public int Index { get; init; }
    public double UsagePercent { get; init; }
    public int CurrentMhz { get; init; }
    public int MaxMhz { get; init; }
    public string ParkingState { get; init; } = "Unknown";
    public string DisplayName => Index < 0 ? "CPU" : $"CPU {Index}";
}
