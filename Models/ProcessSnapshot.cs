namespace ZestResourceOptimizer.Models;

public sealed class ProcessSnapshot
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ExecutableName { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public double CpuPercent { get; init; }
    public double GpuPercent { get; init; }
    public double MemoryMb { get; init; }
    public double CommitMb { get; init; }
    public double VramMb { get; init; }
    public int AiScore { get; init; }
    public string Priority { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public bool IsProtected { get; init; }
    public bool IsCritical { get; init; }
    public bool IsStarCitizen { get; init; }
    public bool IsBackground { get; init; }
}
