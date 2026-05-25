namespace ANEVRED.Models;

public sealed class ScreenTranslationResult
{
    public string OriginalText { get; init; } = string.Empty;
    public string TranslatedText { get; init; } = string.Empty;
    public IReadOnlyList<ScreenTranslationSegment> Segments { get; init; } = Array.Empty<ScreenTranslationSegment>();
    public string Status { get; init; } = string.Empty;
    public bool IsAvailable { get; init; }
}

public sealed class ScreenTranslationSegment
{
    public string OriginalText { get; init; } = string.Empty;
    public string TranslatedText { get; init; } = string.Empty;
    public double Left { get; init; }
    public double Top { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}
