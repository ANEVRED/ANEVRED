using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ANEVRED.Models;
using Forms = System.Windows.Forms;

namespace ANEVRED;

public partial class TranslationOverlayWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExNoactivate = 0x08000000;

    private double _regionLocalLeft;
    private double _regionLocalTop;
    private double _regionWidth;
    private double _regionHeight;

    public TranslationOverlayWindow()
    {
        InitializeComponent();
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        RegionFrame.Opacity = 0;
        SourceInitialized += (_, _) => MakeClickThrough();
    }

    public void SetRegion(Rect region)
    {
        var selectedScreen = Forms.Screen.FromRectangle(new System.Drawing.Rectangle(
            (int)Math.Round(region.Left),
            (int)Math.Round(region.Top),
            Math.Max(1, (int)Math.Round(region.Width)),
            Math.Max(1, (int)Math.Round(region.Height))));
        var screenLeft = selectedScreen.Bounds.Left;
        var screenTop = selectedScreen.Bounds.Top;
        var screenWidth = selectedScreen.Bounds.Width;
        var screenHeight = selectedScreen.Bounds.Height;
        Left = screenLeft;
        Top = screenTop;
        Width = screenWidth;
        Height = screenHeight;

        var frameLeft = Math.Max(screenLeft, Math.Min(region.Left, screenLeft + screenWidth - 120));
        var frameTop = Math.Max(screenTop, Math.Min(region.Top, screenTop + screenHeight - 80));
        var frameWidth = Math.Min(Math.Max(120, region.Width), screenLeft + screenWidth - frameLeft);
        var frameHeight = Math.Min(Math.Max(80, region.Height), screenTop + screenHeight - frameTop);
        var localLeft = frameLeft - screenLeft;
        var localTop = frameTop - screenTop;

        _regionLocalLeft = localLeft;
        _regionLocalTop = localTop;
        _regionWidth = frameWidth;
        _regionHeight = frameHeight;

        RegionFrame.Width = frameWidth;
        RegionFrame.Height = frameHeight;
        Canvas.SetLeft(RegionFrame, localLeft);
        Canvas.SetTop(RegionFrame, localTop);

        TextPanel.Width = frameWidth;
        TextPanel.Height = frameHeight;
        Canvas.SetLeft(TextPanel, localLeft);
        Canvas.SetTop(TextPanel, localTop);

        SegmentCanvas.Width = frameWidth;
        SegmentCanvas.Height = frameHeight;
        SegmentCanvas.ClipToBounds = true;
        Canvas.SetLeft(SegmentCanvas, localLeft);
        Canvas.SetTop(SegmentCanvas, localTop);
    }

    public void SetText(string translatedText, string status)
    {
        SegmentCanvas.Children.Clear();
        TextPanel.Visibility = Visibility.Visible;
        TranslationText.Text = SanitizeOverlayText(translatedText);
    }

    public void SetStructuredText(IReadOnlyList<ScreenTranslationSegment> segments, string fallbackText, string status)
    {
        SegmentCanvas.Children.Clear();
        if (segments.Count == 0 && TryParseSegmentsFromJson(fallbackText, out var parsedSegments))
        {
            segments = parsedSegments;
        }

        if (segments.Count == 0)
        {
            // Last safety net: never show raw OCR/translation JSON as one big text block.
            SetText(IsJsonLike(fallbackText) ? string.Empty : fallbackText, status);
            return;
        }

        TranslationText.Text = string.Empty;
        TextPanel.Visibility = Visibility.Collapsed;
        var renderSegments = NormalizeSegmentsForOverlay(segments);

        foreach (var segment in renderSegments)
        {
            if (string.IsNullOrWhiteSpace(segment.TranslatedText))
            {
                continue;
            }

            var left = Math.Clamp(segment.Left, 0, Math.Max(0, _regionWidth - 20));
            var top = Math.Clamp(segment.Top, 0, Math.Max(0, _regionHeight - 16));
            var width = Math.Max(32, Math.Min(segment.Width, Math.Max(32, _regionWidth - left - 2)));
            var height = Math.Max(16, Math.Min(segment.Height, Math.Max(16, _regionHeight - top - 2)));
            var fontSize = CalculateFontSize(segment);
            var isParagraph = IsParagraphSegment(segment);
            var columnWidth = EstimateColumnWidth(renderSegments, segment);
            var readableWidth = Math.Min(
                Math.Min(Math.Max(32, _regionWidth - left - 2), columnWidth),
                Math.Max(width, EstimateReadableWidth(segment, fontSize, isParagraph)));
            var readableHeight = Math.Min(
                Math.Max(16, _regionHeight - top - 2),
                Math.Max(height + 16, EstimateReadableHeight(segment.TranslatedText, readableWidth, fontSize)));

            var textBlock = new TextBlock
            {
                Text = segment.TranslatedText,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None,
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(238, 5, 10, 16)),
                Padding = isParagraph ? new Thickness(7, 4, 7, 5) : new Thickness(5, 2, 5, 3),
                Width = readableWidth,
                Height = readableHeight,
                ClipToBounds = true,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            textBlock.LineHeight = textBlock.FontSize * 1.16;
            TextOptions.SetTextFormattingMode(textBlock, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(textBlock, TextRenderingMode.ClearType);

            Canvas.SetLeft(textBlock, left);
            Canvas.SetTop(textBlock, top);
            SegmentCanvas.Children.Add(textBlock);
        }
    }

    private IReadOnlyList<ScreenTranslationSegment> NormalizeSegmentsForOverlay(IReadOnlyList<ScreenTranslationSegment> segments)
    {
        if (segments.Count == 0 || _regionWidth <= 0 || _regionHeight <= 0)
        {
            return segments;
        }

        var maxRight = segments.Max(segment => segment.Left + Math.Max(1, segment.Width));
        var maxBottom = segments.Max(segment => segment.Top + Math.Max(1, segment.Height));
        var minLeft = segments.Min(segment => segment.Left);
        var minTop = segments.Min(segment => segment.Top);

        // Normal service path already gives region-local coordinates. This fallback handles OCR JSON
        // or older cache entries that still contain screenshot/screen coordinates.
        if (maxRight <= _regionWidth * 1.25 && maxBottom <= _regionHeight * 1.25 && minLeft >= -8 && minTop >= -8)
        {
            return segments;
        }

        var sourceWidth = Math.Max(1, maxRight - minLeft);
        var sourceHeight = Math.Max(1, maxBottom - minTop);
        var scaleX = _regionWidth / sourceWidth;
        var scaleY = _regionHeight / sourceHeight;

        return segments.Select(segment => new ScreenTranslationSegment
        {
            OriginalText = segment.OriginalText,
            TranslatedText = segment.TranslatedText,
            Left = (segment.Left - minLeft) * scaleX,
            Top = (segment.Top - minTop) * scaleY,
            Width = Math.Max(24, segment.Width * scaleX),
            Height = Math.Max(14, segment.Height * scaleY)
        }).ToArray();
    }

    private static string SanitizeOverlayText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Safety guard: OCR/translation JSON must never be rendered as visible overlay text.
        // If parsing failed somewhere upstream, showing nothing is better than covering the screen
        // with { "Text": ..., "lines": ... }.
        return IsJsonLike(text) ? string.Empty : text;
    }

    private static bool TryParseSegmentsFromJson(string text, out IReadOnlyList<ScreenTranslationSegment> segments)
    {
        segments = Array.Empty<ScreenTranslationSegment>();
        if (string.IsNullOrWhiteSpace(text) || !IsJsonLike(text))
        {
            return false;
        }

        try
        {
            var first = text.IndexOf('{');
            var last = text.LastIndexOf('}');
            if (first < 0 || last <= first)
            {
                return false;
            }

            using var document = JsonDocument.Parse(text[first..(last + 1)]);
            var root = document.RootElement;
            if (!TryGetPropertyAny(root, out var linesElement, "lines", "Lines", "строки", "Строки", "zeilen", "Zeilen")
                || linesElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var list = new List<ScreenTranslationSegment>();
            foreach (var item in linesElement.EnumerateArray())
            {
                var lineText = GetStringPropertyAny(item, "text", "Text", "Текст", "текст");
                if (string.IsNullOrWhiteSpace(lineText))
                {
                    continue;
                }

                list.Add(new ScreenTranslationSegment
                {
                    OriginalText = lineText.Trim(),
                    TranslatedText = lineText.Trim(),
                    Left = GetDoublePropertyAny(item, "left", "Left", "LEFT", "лево", "Лево", "links", "x", "X"),
                    Top = GetDoublePropertyAny(item, "top", "Top", "TOP", "верх", "Верх", "верхний", "Верхний", "oben", "y", "Y"),
                    Width = Math.Max(24, GetDoublePropertyAny(item, "width", "Width", "WIDTH", "ширина", "Ширина", "breite", "w", "W")),
                    Height = Math.Max(12, GetDoublePropertyAny(item, "height", "Height", "HEIGHT", "высота", "Высота", "hoehe", "Höhe", "h", "H"))
                });
            }

            segments = list;
            return list.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsJsonLike(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal);
    }

    private static bool TryGetPropertyAny(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetStringPropertyAny(JsonElement element, params string[] names)
    {
        return TryGetPropertyAny(element, out var value, names) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static double GetDoublePropertyAny(JsonElement element, params string[] names)
    {
        if (!TryGetPropertyAny(element, out var value, names))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var number) => number,
            _ => 0
        };
    }

    public void SetCaptureMode(bool isCapturing)
    {
        RegionFrame.Opacity = isCapturing ? 1 : 0;
        TextPanel.Opacity = isCapturing ? 0 : 1;
        SegmentCanvas.Opacity = isCapturing ? 0 : 1;
    }

    private static double CalculateFontSize(ScreenTranslationSegment segment)
    {
        var byHeight = Math.Clamp(segment.Height * 0.66, 8.5, 14);
        var textPressure = segment.TranslatedText.Length / Math.Max(1.0, segment.OriginalText.Length);
        if (IsCompactLabelSegment(segment))
        {
            return Math.Clamp(byHeight - 0.8, 8.5, 11.5);
        }

        return textPressure > 1.8 ? Math.Max(8.5, byHeight - 1.2) : byHeight;
    }

    private static bool IsParagraphSegment(ScreenTranslationSegment segment)
    {
        return segment.TranslatedText.Length >= 80
            || segment.OriginalText.Contains('\n')
            || segment.Width >= 360;
    }

    private static double EstimateColumnWidth(IReadOnlyList<ScreenTranslationSegment> segments, ScreenTranslationSegment segment)
    {
        var segmentRight = segment.Left + segment.Width;
        var nearestRightColumn = segments
            .Where(other => !ReferenceEquals(other, segment))
            .Where(other => other.Left > segment.Left + Math.Max(24, segment.Width * 0.45))
            .Where(other => RangesOverlap(segment.Top, segment.Top + Math.Max(18, segment.Height), other.Top, other.Top + Math.Max(18, other.Height)))
            .Select(other => other.Left)
            .DefaultIfEmpty(double.PositiveInfinity)
            .Min();

        if (double.IsInfinity(nearestRightColumn))
        {
            return Math.Max(48, segment.Width * 1.55);
        }

        var available = nearestRightColumn - segment.Left - 8;
        return Math.Clamp(available, Math.Max(48, segment.Width * 0.85), Math.Max(48, segment.Width * 1.35));
    }

    private static bool RangesOverlap(double firstStart, double firstEnd, double secondStart, double secondEnd)
    {
        return Math.Min(firstEnd, secondEnd) - Math.Max(firstStart, secondStart) > 0;
    }

    private static bool IsCompactLabelSegment(ScreenTranslationSegment segment)
    {
        return segment.Height <= 24
            && segment.OriginalText.Length <= 42
            && !segment.OriginalText.Contains('\n');
    }

    private static double EstimateReadableWidth(ScreenTranslationSegment segment, double fontSize, bool isParagraph)
    {
        var text = segment.TranslatedText;
        var longestWord = text
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length)
            .DefaultIfEmpty(0)
            .Max();
        var textPressure = text.Length / Math.Max(1.0, segment.OriginalText.Length);
        var lineTarget = text.Length <= 18
            ? text.Length
            : isParagraph
                ? Math.Min(92, Math.Max(longestWord, text.Length / 3))
                : IsCompactLabelSegment(segment)
                    ? Math.Min(34, Math.Max(longestWord, text.Length / 2))
                    : Math.Min(42, Math.Max(longestWord, text.Length / 2));
        var estimated = lineTarget * fontSize * 0.56 + 14;
        var expansion = isParagraph
            ? segment.Width * 1.35
            : IsCompactLabelSegment(segment)
                ? segment.Width * 1.12
                : text.Length > 24 || textPressure > 1.35
                ? segment.Width * 1.65
            : segment.Width;
        return Math.Clamp(Math.Max(estimated, expansion), 36, isParagraph ? 720 : 420);
    }

    private static double EstimateReadableHeight(string text, double width, double fontSize)
    {
        var charsPerLine = Math.Max(6, (int)((width - 12) / Math.Max(1, fontSize * 0.56)));
        var lines = Math.Max(1, (int)Math.Ceiling(text.Length / (double)charsPerLine));
        lines += text.Count(ch => ch == '\n');
        return Math.Clamp(lines * fontSize * 1.32 + 12, 24, 240);
    }

    private void MakeClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExstyle);
        _ = SetWindowLong(handle, GwlExstyle, style | WsExTransparent | WsExToolwindow | WsExNoactivate);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
}
