using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;

namespace ZestResourceOptimizer.Models;

public sealed class MetricCard
{
    public string Title { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public MediaBrush Accent { get; init; } = MediaBrushes.DodgerBlue;
    public IReadOnlyList<double> History { get; init; } = [];
}
