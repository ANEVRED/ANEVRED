using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ZestResourceOptimizer.Models;
using Forms = System.Windows.Forms;

namespace ZestResourceOptimizer;

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

            var textBlock = new TextBlock
            {
                Text = segment.TranslatedText,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = CalculateFontSize(segment),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(245, 5, 10, 16)),
                Padding = new Thickness(3, 1, 3, 1),
                Width = width,
                MaxHeight = Math.Min(_regionHeight - top, Math.Max(height + 10, height * 2.8)),
                ClipToBounds = true,
                IsHitTestVisible = false
            };
            textBlock.LineHeight = textBlock.FontSize * 1.16;

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
        var byHeight = Math.Clamp(segment.Height <= 24 ? segment.Height * 0.78 : 13, 9, 15);
        var textPressure = segment.TranslatedText.Length / Math.Max(1.0, segment.OriginalText.Length);
        return textPressure > 1.8 ? Math.Max(9, byHeight - 1.5) : byHeight;
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
