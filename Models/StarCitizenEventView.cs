namespace ZestResourceOptimizer.Models;

public sealed class StarCitizenEventView
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Type { get; init; } = string.Empty;
    public string SessionTime { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
}
