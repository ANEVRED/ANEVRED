namespace ZestResourceOptimizer.Models;

public sealed class ScreenTranslationResult
{
    public string OriginalText { get; init; } = string.Empty;
    public string TranslatedText { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public bool IsAvailable { get; init; }
}
