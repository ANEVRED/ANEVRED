namespace ZestResourceOptimizer.Models;

public sealed class SessionLearningSample
{
    public DateTime Time { get; init; } = DateTime.UtcNow;
    public double CpuPercent { get; init; }
    public double RamPercent { get; init; }
    public double GpuPercent { get; init; }
    public double VramPercent { get; init; }
    public bool StarCitizenRunning { get; init; }
    public IReadOnlyList<string> TopProcesses { get; init; } = [];
}
