namespace ZestResourceOptimizer.Models;

public sealed class NavigationItem
{
    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public bool IsActive { get; init; }
}
