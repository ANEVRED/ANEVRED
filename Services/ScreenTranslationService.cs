using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ANEVRED.Models;

namespace ANEVRED.Services;

public sealed class ScreenTranslationService : IDisposable
{
    private readonly Action<string>? _log;
    private string _lastImageHash = string.Empty;
    private string _lastTargetLanguage = string.Empty;
    private string _lastOriginalText = string.Empty;
    private string _lastTranslatedText = string.Empty;
    private IReadOnlyList<ScreenTranslationSegment> _lastSegments = Array.Empty<ScreenTranslationSegment>();
    private DateTime _lastOcr = DateTime.MinValue;
    private readonly ChromeTranslationService _chromeTranslation;
    private readonly Action? _ocrCompleted;

    public ScreenTranslationService(Action<string>? log = null, Action? ocrCompleted = null)
    {
        _log = log;
        _ocrCompleted = ocrCompleted;
        _chromeTranslation = new ChromeTranslationService(log);
    }

    public void CancelActiveTranslation()
    {
    }

    public Task<bool> CheckChromeTranslationAsync(
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        return _chromeTranslation.CheckAsync(targetLanguage, cancellationToken);
    }

    public async Task<ScreenTranslationResult> TranslateRegionAsync(
        Rectangle region,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        _log?.Invoke($"OCR start: region={region.Left},{region.Top},{region.Width}x{region.Height}, target={targetLanguage}");
        if (region.Width < 20 || region.Height < 20)
        {
            _log?.Invoke("OCR skipped: region too small.");
            return new ScreenTranslationResult
            {
                Status = "Bereich zu klein.",
                IsAvailable = false
            };
        }

        using var bitmap = CaptureRegion(region);
        using var preparedBitmap = PrepareForOcr(bitmap);
        var imageHash = HashBitmap(preparedBitmap);
        var normalizedTargetLanguage = NormalizeTargetLanguage(targetLanguage);
        if (imageHash == _lastImageHash
            && normalizedTargetLanguage.Equals(_lastTargetLanguage, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_lastOriginalText))
        {
            _log?.Invoke($"Screen unchanged, using cached translation: chars={_lastOriginalText.Length}");
            return BuildResult(_lastOriginalText, _lastTranslatedText, _lastSegments, targetLanguage, cached: true, shortened: false);
        }

        _lastImageHash = imageHash;
        _lastTargetLanguage = normalizedTargetLanguage;
        _lastOcr = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveDebugCapture(bitmap, "last-ocr-raw.png");
            SaveDebugCapture(preparedBitmap, "last-ocr-capture.png");

            // Try native pixels first. Upscaling helps some tiny UI text, but it can also make
            // anti-aliased game fonts look blocky/soft and shift OCR coordinates. If native OCR
            // succeeds, we keep native coordinates and the overlay maps much more accurately.
            var ocrBitmapWidth = bitmap.Width;
            var ocrBitmapHeight = bitmap.Height;
            var ocrResult = await ReadTextFromBitmapWithWindowsOcrAsync(bitmap, "raw-native");
            cancellationToken.ThrowIfCancellationRequested();

            if (IsOcrEmpty(ocrResult))
            {
                var retryVariants = new (string Name, Func<Bitmap> Factory)[]
                {
                    ("crisp-2x", () => (Bitmap)preparedBitmap.Clone()),
                    ("relaxed-2x", () => PrepareForOcrRelaxed(bitmap)),
                    ("ui-3x", () => PrepareForUiTextOcr(bitmap)),
                    ("raw-3x", () => PrepareRawUpscaledForOcr(bitmap)),
                    ("contrast-3x", () => PrepareContrastForOcr(bitmap))
                };

                foreach (var variant in retryVariants)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var retryBitmap = variant.Factory();
                    SaveDebugCapture(retryBitmap, $"last-ocr-{variant.Name}.png");
                    var retry = await ReadTextFromBitmapWithWindowsOcrAsync(retryBitmap, variant.Name);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsOcrEmpty(retry))
                    {
                        ocrResult = retry;
                        ocrBitmapWidth = retryBitmap.Width;
                        ocrBitmapHeight = retryBitmap.Height;
                        _log?.Invoke($"OCR retry succeeded with {variant.Name}: chars={retry.Text.Length}, lines={retry.Lines.Count}");
                        break;
                    }

                    _log?.Invoke($"OCR retry {variant.Name} returned no text.");
                }
            }
            else
            {
                _log?.Invoke($"OCR native succeeded: chars={ocrResult.Text.Length}, lines={ocrResult.Lines.Count}");
            }

            var originalText = ocrResult.Text;
            if (ocrResult.Lines.Count == 0 && IsJsonLike(originalText))
            {
                // Defensive repair for unexpected OCR stdout shapes: never pass raw OCR JSON
                // to Chrome, because Chrome will translate keys/metadata and destroy structure.
                var repaired = ParseOcrResult(originalText);
                if (repaired.Lines.Count > 0 || !string.IsNullOrWhiteSpace(repaired.Text))
                {
                    ocrResult = repaired;
                    originalText = ocrResult.Text;
                    _log?.Invoke($"OCR JSON repaired before translation: chars={originalText.Length}, lines={ocrResult.Lines.Count}");
                }
            }

            _log?.Invoke($"OCR finished: chars={originalText.Length}, lines={ocrResult.Lines.Count}");
            _ocrCompleted?.Invoke();
            var segments = await TranslateStructuredLinesAsync(ocrResult.Lines, ocrBitmapWidth, ocrBitmapHeight, region, targetLanguage, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var shortened = false;
            string translatedText;
            if (segments.Count > 0)
            {
                translatedText = string.Join(Environment.NewLine, segments.Select(segment => segment.TranslatedText));
            }
            else
            {
                var preparedText = PrepareTextForChromeTranslation(originalText);
                shortened = preparedText.WasShortened;
                if (preparedText.WasShortened)
                {
                    _log?.Invoke($"Translation text shortened for Chrome translation: {originalText.Length} -> {preparedText.Text.Length} chars.");
                }

                translatedText = await TranslateTextAsync(preparedText.Text, targetLanguage, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }

            _log?.Invoke($"Translation finished: chars={translatedText.Length}, segments={segments.Count}");

            _lastOriginalText = originalText;
            _lastTranslatedText = translatedText;
            _lastSegments = segments;
            return BuildResult(originalText, translatedText, segments, targetLanguage, cached: false, shortened);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke("ScreenTranslation failed: " + ex.Message);
            return new ScreenTranslationResult
            {
                Status = "OCR/Übersetzung fehlgeschlagen: " + ex.Message,
                IsAvailable = false
            };
        }
    }

    private static Bitmap CaptureRegion(Rectangle region)
    {
        var bitmap = new Bitmap(region.Width, region.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }


    private static void ConfigureOcrUpscaleGraphics(Graphics graphics)
    {
        // OCR needs crisp glyph edges. HighQualityBicubic/Bilinear makes game/UI fonts
        // look nicer to humans but blurs letter borders and can make Windows OCR miss text.
        // NearestNeighbor keeps the original pixel geometry during 2x/3x scaling.
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
    }

    private static Bitmap PrepareForOcrRelaxed(Bitmap source)
    {
        var scale = source.Width < 1400 ? 2 : 1;
        var width = Math.Min(source.Width * scale, 2600);
        var height = Math.Min(source.Height * scale, 2600);
        var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(scaled);
        graphics.Clear(Color.Black);
        ConfigureOcrUpscaleGraphics(graphics);
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));

        // Mild contrast only. Keeps anti-aliased UI text readable for Windows OCR.
        using var attributes = new ImageAttributes();
        return scaled;
    }

    private static Bitmap PrepareRawUpscaledForOcr(Bitmap source)
    {
        return UpscaleBitmap(source, source.Width < 1800 ? 3 : 2, Color.Black);
    }

    private static Bitmap PrepareForUiTextOcr(Bitmap source)
    {
        var scaled = UpscaleBitmap(source, source.Width < 1800 ? 3 : 2, Color.Black);
        var rect = new Rectangle(0, 0, scaled.Width, scaled.Height);
        var data = scaled.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                var ptr = (byte*)data.Scan0;
                for (var y = 0; y < data.Height; y++)
                {
                    var row = ptr + y * data.Stride;
                    for (var x = 0; x < data.Width; x++)
                    {
                        var offset = x * 3;
                        var b = row[offset];
                        var g = row[offset + 1];
                        var r = row[offset + 2];
                        var gray = (int)(r * 0.299 + g * 0.587 + b * 0.114);
                        gray = Math.Clamp((gray - 70) * 3 / 2 + 120, 0, 255);
                        row[offset] = row[offset + 1] = row[offset + 2] = (byte)gray;
                    }
                }
            }
        }
        finally
        {
            scaled.UnlockBits(data);
        }

        return scaled;
    }

    private static Bitmap PrepareContrastForOcr(Bitmap source)
    {
        var scaled = UpscaleBitmap(source, source.Width < 1800 ? 3 : 2, Color.Black);
        var rect = new Rectangle(0, 0, scaled.Width, scaled.Height);
        var data = scaled.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                var ptr = (byte*)data.Scan0;
                for (var y = 0; y < data.Height; y++)
                {
                    var row = ptr + y * data.Stride;
                    for (var x = 0; x < data.Width; x++)
                    {
                        var offset = x * 3;
                        var b = row[offset];
                        var g = row[offset + 1];
                        var r = row[offset + 2];
                        var gray = (int)(r * 0.299 + g * 0.587 + b * 0.114);
                        gray = Math.Clamp((gray - 90) * 2 + 150, 0, 255);
                        row[offset] = row[offset + 1] = row[offset + 2] = (byte)gray;
                    }
                }
            }
        }
        finally
        {
            scaled.UnlockBits(data);
        }

        return scaled;
    }

    private static Bitmap UpscaleBitmap(Bitmap source, int scale, Color background)
    {
        scale = Math.Clamp(scale, 1, 4);
        var width = Math.Min(source.Width * scale, 3200);
        var height = Math.Min(source.Height * scale, 3200);
        var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(scaled);
        graphics.Clear(background);
        ConfigureOcrUpscaleGraphics(graphics);
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return scaled;
    }

    private static Bitmap PrepareForOcr(Bitmap source)
    {
        var scale = source.Width < 1400 ? 2 : 1;
        var width = Math.Min(source.Width * scale, 2600);
        var height = Math.Min(source.Height * scale, 2600);
        var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(scaled);
        graphics.Clear(Color.Black);
        ConfigureOcrUpscaleGraphics(graphics);
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));

        var rect = new Rectangle(0, 0, scaled.Width, scaled.Height);
        var data = scaled.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                var ptr = (byte*)data.Scan0;
                for (var y = 0; y < data.Height; y++)
                {
                    var row = ptr + y * data.Stride;
                    for (var x = 0; x < data.Width; x++)
                    {
                        var offset = x * 3;
                        var b = row[offset];
                        var g = row[offset + 1];
                        var r = row[offset + 2];
                        var gray = (int)(r * 0.299 + g * 0.587 + b * 0.114);
                        gray = Math.Clamp((gray - 95) * 2 + 128, 0, 255);
                        row[offset] = row[offset + 1] = row[offset + 2] = (byte)gray;
                    }
                }
            }
        }
        finally
        {
            scaled.UnlockBits(data);
        }

        return scaled;
    }

    private void SaveDebugCapture(Bitmap bitmap, string fileName)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ANEVRED",
                "OcrDebug");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            bitmap.Save(path, ImageFormat.Png);
            _log?.Invoke("OCR debug capture saved: " + path);
        }
        catch (Exception ex)
        {
            _log?.Invoke("OCR debug capture failed: " + ex.Message);
        }
    }

    private static string HashBitmap(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static async Task<OcrReadResult> ReadTextFromBitmapWithWindowsOcrAsync(Bitmap bitmap, string variantName)
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"anevred-ocr-{variantName}-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            bitmap.Save(imagePath, ImageFormat.Png);
            return await ReadTextWithWindowsOcrAsync(imagePath);
        }
        finally
        {
            try { File.Delete(imagePath); } catch { }
        }
    }

    private static async Task<OcrReadResult> ReadTextWithWindowsOcrAsync(string imagePath)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Tools", "WindowsOcr.ps1");
        if (!File.Exists(scriptPath))
        {
            scriptPath = Path.Combine(AppContext.BaseDirectory, "WindowsOcr.ps1");
        }

        if (!File.Exists(scriptPath))
        {
            return new OcrReadResult(string.Empty, Array.Empty<OcrLine>());
        }

        var output = await RunProcessAsync(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -ImagePath \"{imagePath}\" -LanguageTag auto -Json",
            timeoutMs: 16000);
        return ParseOcrResult(output);
    }

    private static bool IsOcrEmpty(OcrReadResult result)
    {
        return result.Lines.Count == 0 && string.IsNullOrWhiteSpace(result.Text);
    }

    private async Task<string> TranslateTextAsync(
        string originalText,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(originalText) || targetLanguage.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            _log?.Invoke(string.IsNullOrWhiteSpace(originalText)
                ? "Chrome translation skipped: OCR text is empty."
                : "Chrome translation skipped: target is English.");
            return originalText;
        }

        if (IsJsonLike(originalText))
        {
            _log?.Invoke("Chrome translation blocked: input looks like OCR JSON. Structural metadata must not be translated.");
            var parsed = ParseOcrResult(originalText);
            return parsed.Lines.Count > 0
                ? string.Join(Environment.NewLine, parsed.Lines.Select(line => line.Text))
                : string.Empty;
        }

        _log?.Invoke($"Chrome translation request: target={targetLanguage}, chars={originalText.Length}.");
        return await _chromeTranslation.TranslateAsync(originalText, targetLanguage, cancellationToken);
    }

    private async Task<IReadOnlyList<ScreenTranslationSegment>> TranslateStructuredLinesAsync(
        IReadOnlyList<OcrLine> lines,
        int preparedWidth,
        int preparedHeight,
        Rectangle captureRegion,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0 || targetLanguage.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<ScreenTranslationSegment>();
        }

        var usefulLines = lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .Where(line => !IsLikelyNavigationLine(line.Text))
            .OrderBy(line => line.Top)
            .ThenBy(line => line.Left)
            .Take(80)
            .ToArray();
        if (usefulLines.Length == 0)
        {
            return Array.Empty<ScreenTranslationSegment>();
        }

        // Important: don't translate every OCR row independently. On web/game UIs a paragraph is
        // often split into many short OCR rows; translating row-by-row destroys meaning and then
        // the longer translated rows overlap each other. We first rebuild visual blocks and translate
        // each block separately. Do NOT batch blocks with markers here: Chrome sometimes removes,
        // translates, or reorders marker tokens, which caused the whole translation to be rendered
        // into the first box.
        var blocks = BuildVisualTextBlocks(usefulLines).Take(35).ToArray();
        if (blocks.Length == 0)
        {
            return Array.Empty<ScreenTranslationSegment>();
        }

        var xScale = preparedWidth <= 0 ? 1 : (double)captureRegion.Width / preparedWidth;
        var yScale = preparedHeight <= 0 ? 1 : (double)captureRegion.Height / preparedHeight;
        var result = new List<ScreenTranslationSegment>();
        var failedTranslations = 0;
        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var originalBlock = block.Text.Trim();
            if (string.IsNullOrWhiteSpace(originalBlock))
            {
                continue;
            }

            var translated = await TranslateTextAsync(originalBlock, targetLanguage, cancellationToken);
            translated = CleanTranslatedBlockText(translated, originalBlock, targetLanguage);
            if (string.IsNullOrWhiteSpace(translated))
            {
                failedTranslations++;
                translated = originalBlock;
            }
            else if (LooksLikeUntranslatedEcho(translated, originalBlock, targetLanguage))
            {
                failedTranslations++;
            }

            if (ShouldSuppressShortGameProperNoun(originalBlock))
            {
                _log?.Invoke("Structured translation skipped proper noun/button text: " + TrimForLog(originalBlock));
                continue;
            }

            if (ShouldSuppressActionLabel(originalBlock))
            {
                _log?.Invoke("Structured translation skipped action/navigation text: " + TrimForLog(originalBlock));
                continue;
            }

            result.Add(new ScreenTranslationSegment
            {
                OriginalText = originalBlock,
                TranslatedText = translated,
                Left = Math.Max(0, block.Left * xScale),
                Top = Math.Max(0, block.Top * yScale),
                Width = Math.Max(36, block.Width * xScale),
                Height = Math.Max(18, block.Height * yScale)
            });
        }

        if (failedTranslations > 0)
        {
            _log?.Invoke($"Structured translation partial fallback: {failedTranslations}/{result.Count} blocks used original OCR text.");
        }

        return result;
    }


    private static string CleanTranslatedBlockText(string translated, string original, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(translated))
        {
            return string.Empty;
        }

        var cleaned = translated
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        cleaned = Regex.Replace(cleaned, @"@+\s*\d{1,4}\s*@+", string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(original))
        {
            return CollapseExcessBlankLines(cleaned);
        }

        // Chrome/Google Translate can sometimes return source + translation together,
        // especially when the source was copied from a complex UI. Remove exact source
        // fragments so the overlay window contains only translated text.
        var originalLines = original
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .SelectMany(line => line.Split(new[] { " | ", " • " }, StringSplitOptions.None))
            .Select(line => line.Trim())
            .Where(line => line.Length >= 4)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(line => line.Length)
            .ToArray();

        foreach (var line in originalLines)
        {
            cleaned = RemoveLiteralIgnoreCase(cleaned, line).Trim();
        }

        // Chrome may return both translation and the original source text in the same result
        // (common when the copy buffer contains hidden DOM text or marker text). Remove source echoes
        // line-by-line with a fuzzy comparison against the OCR source block.
        cleaned = RemoveSourceEchoLines(cleaned, original, targetLanguage);

        // If a whole original paragraph survived with whitespace differences, remove it too.
        var normalizedOriginal = NormalizeForComparison(original);
        var normalizedCleaned = NormalizeForComparison(cleaned);
        if (normalizedOriginal.Length >= 12 && normalizedCleaned.Contains(normalizedOriginal, StringComparison.OrdinalIgnoreCase))
        {
            cleaned = RemoveByNormalizedMatch(cleaned, original).Trim();
        }

        return CollapseExcessBlankLines(cleaned);
    }

    private static string RemoveSourceEchoLines(string translated, string original, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(translated) || string.IsNullOrWhiteSpace(original))
        {
            return translated;
        }

        var originalKey = ToComparisonKey(original);
        var wantsCyrillic = targetLanguage.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
            || targetLanguage.StartsWith("uk", StringComparison.OrdinalIgnoreCase);
        var wholeTextHasCyrillic = ContainsCyrillic(translated);
        var lines = translated
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var kept = new List<string>();
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var lineKey = ToComparisonKey(line);
            if (lineKey.Length >= 10 && (originalKey.Contains(lineKey, StringComparison.OrdinalIgnoreCase)
                || lineKey.Contains(originalKey, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (LooksLikeOriginalEcho(line, original))
            {
                continue;
            }

            // EN -> RU/UK often comes back as: translated Cyrillic lines + original English lines.
            // Once a Cyrillic translation is present, long Latin-only lines are almost always the
            // original echo and should not be rendered in the overlay.
            if (wantsCyrillic && wholeTextHasCyrillic && !ContainsCyrillic(line) && CountLatinLetters(line) >= 8)
            {
                continue;
            }

            kept.Add(line);
        }

        return string.Join("\n", kept).Trim();
    }

    private static bool LooksLikeOriginalEcho(string line, string original)
    {
        var lineWords = WordSet(line);
        if (lineWords.Count < 2)
        {
            return false;
        }

        var originalWords = WordSet(original);
        if (originalWords.Count == 0)
        {
            return false;
        }

        var common = lineWords.Count(word => originalWords.Contains(word));
        return common >= 2 && common / (double)lineWords.Count >= 0.62;
    }

    private static bool LooksLikeUntranslatedEcho(string translated, string original, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(translated) || string.IsNullOrWhiteSpace(original))
        {
            return false;
        }

        var wantsCyrillic = targetLanguage.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
            || targetLanguage.StartsWith("uk", StringComparison.OrdinalIgnoreCase);
        if (wantsCyrillic && ContainsCyrillic(translated))
        {
            return false;
        }

        var translatedKey = ToComparisonKey(translated);
        var originalKey = ToComparisonKey(original);
        if (translatedKey.Length < 6 || originalKey.Length < 6)
        {
            return string.Equals(translated.Trim(), original.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        return translatedKey.Equals(originalKey, StringComparison.OrdinalIgnoreCase)
            || LooksLikeOriginalEcho(translated, original);
    }

    private static bool ShouldSuppressShortGameProperNoun(string original)
    {
        if (string.IsNullOrWhiteSpace(original) || original.Length > 42)
        {
            return false;
        }

        var normalized = original.Trim();
        if (Regex.IsMatch(normalized, @"^\d+([.,:]\d+)?\s*(s|min|h|d|SCU|aUEC|UEC|%|/|\-)?$", RegexOptions.IgnoreCase))
        {
            return true;
        }

        var knownProperNames = new[]
        {
            "Aegis", "Anvil", "Argo", "Origin", "Drake", "Crusader", "MISC", "RSI",
            "Consolidated", "Outland", "Banu", "Xi'an", "Xian", "Vanduul", "Tevarin",
            "Area18", "ArcCorp", "Lorville", "Orison", "MicroTech", "Hurston", "Crusader",
            "Pyro", "Stanton", "Red Wind", "Linehaul", "Riker"
        };

        return knownProperNames.Any(name => normalized.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldSuppressActionLabel(string original)
    {
        if (string.IsNullOrWhiteSpace(original) || original.Length > 36)
        {
            return false;
        }

        var normalized = Regex.Replace(original.Trim(), @"\s+", " ");
        var knownActions = new[]
        {
            "ASSEMBLE YOUR ALLIES",
            "ENTER DEFENSECON",
            "LAUNCH GAME",
            "GAME IS RUNNING",
            "MARK ALL READ",
            "ACCEPT OFFER",
            "RETRIEVE",
            "CLAIM",
            "INSURE LOADOUT"
        };

        if (knownActions.Any(action => normalized.Equals(action, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var letters = normalized.Where(char.IsLetter).ToArray();
        if (letters.Length < 8)
        {
            return false;
        }

        var upper = letters.Count(char.IsUpper);
        var wordCount = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return wordCount <= 4 && upper >= letters.Length * 0.75;
    }

    private static string TrimForLog(string value)
    {
        value = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        return value.Length <= 90 ? value : value[..90] + "...";
    }

    private static HashSet<string> WordSet(string value)
    {
        return Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{Nd}]{2,}")
            .Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string ToComparisonKey(string value)
    {
        return Regex.Replace(value ?? string.Empty, @"[^\p{L}\p{Nd}]+", string.Empty).ToLowerInvariant();
    }

    private static bool ContainsCyrillic(string value)
    {
        return !string.IsNullOrEmpty(value) && value.Any(ch => ch >= '\u0400' && ch <= '\u04FF');
    }

    private static int CountLatinLetters(string value)
    {
        return string.IsNullOrEmpty(value) ? 0 : value.Count(ch => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'));
    }

    private static string RemoveLiteralIgnoreCase(string text, string value)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(value))
        {
            return text;
        }

        var escaped = Regex.Escape(value);
        return Regex.Replace(text, escaped, string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string RemoveByNormalizedMatch(string text, string original)
    {
        // Conservative fallback: if the cleaned text starts or ends with the original block
        // ignoring whitespace, cut that side. This avoids removing legitimate translated words.
        var textNorm = NormalizeForComparison(text);
        var origNorm = NormalizeForComparison(original);
        if (origNorm.Length < 12)
        {
            return text;
        }

        if (textNorm.StartsWith(origNorm, StringComparison.OrdinalIgnoreCase))
        {
            return text.Length > original.Length ? text[original.Length..] : string.Empty;
        }

        if (textNorm.EndsWith(origNorm, StringComparison.OrdinalIgnoreCase))
        {
            return text.Length > original.Length ? text[..^original.Length] : string.Empty;
        }

        return text;
    }

    private static string NormalizeForComparison(string value)
    {
        return Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    }

    private static string CollapseExcessBlankLines(string value)
    {
        value = Regex.Replace(value ?? string.Empty, @"[ \t]+", " ");
        value = Regex.Replace(value, @"\n{3,}", "\n\n");
        return value.Trim();
    }

    private static IReadOnlyList<OcrLayoutBlock> BuildVisualTextBlocks(IReadOnlyList<OcrLine> lines)
    {
        var ordered = lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .OrderBy(line => line.Top)
            .ThenBy(line => line.Left)
            .ToArray();
        if (ordered.Length == 0)
        {
            return Array.Empty<OcrLayoutBlock>();
        }

        var averageHeight = Math.Max(8, ordered.Average(line => Math.Max(1, line.Height)));

        // IMPORTANT: build paragraphs, not one large region block.
        // Previous versions allowed too much vertical distance and searched all existing blocks,
        // so a hero section could become one huge translation box. That destroyed the original
        // layout and made translated text appear in the wrong place. We now walk in reading order
        // and split on real paragraph/card gaps, column changes and font-size changes.
        var paragraphGapLimit = Math.Clamp(averageHeight * 0.75, 5, 14);
        var blocks = new List<List<OcrLine>>();
        List<OcrLine>? current = null;

        foreach (var line in ordered)
        {
            if (current is null || current.Count == 0)
            {
                current = new List<OcrLine> { line };
                blocks.Add(current);
                continue;
            }

            var last = current[^1];
            var verticalGap = line.Top - (last.Top + Math.Max(1, last.Height));
            var sameColumn = LooksLikeSameTextColumn(current, line, averageHeight);
            var largeFontChange = HasLargeFontChange(current, line);

            // Same-top but far-left text usually belongs to another card/column, not the previous block.
            // Negative gaps happen when OCR returns another column with the same y-range.
            var mustStartNewBlock = verticalGap < -averageHeight * 0.35
                || verticalGap > paragraphGapLimit
                || !sameColumn
                || (largeFontChange && verticalGap > Math.Max(3, averageHeight * 0.20));

            if (mustStartNewBlock)
            {
                current = new List<OcrLine> { line };
                blocks.Add(current);
            }
            else
            {
                current.Add(line);
            }
        }

        return blocks
            .Select(MakeLayoutBlock)
            .Where(block => !string.IsNullOrWhiteSpace(block.Text))
            .ToArray();
    }

    private static bool LooksLikeSameTextColumn(IReadOnlyList<OcrLine> block, OcrLine line, double averageHeight)
    {
        var blockLeft = block.Min(item => item.Left);
        var blockRight = block.Max(item => item.Left + item.Width);
        var blockWidth = Math.Max(1, blockRight - blockLeft);
        var lineRight = line.Left + line.Width;
        var horizontalOverlap = Math.Min(blockRight, lineRight) - Math.Max(blockLeft, line.Left);
        var overlapRatio = horizontalOverlap / Math.Max(1, Math.Min(blockWidth, Math.Max(1, line.Width)));
        var leftDelta = Math.Abs(line.Left - blockLeft);

        // Wrapped paragraph rows normally keep the same left edge or overlap strongly.
        // Card grids/nav rows do not; keep those as separate blocks.
        return leftDelta <= Math.Max(18, averageHeight * 1.5)
            || overlapRatio >= 0.55
            || (line.Left >= blockLeft - 6 && line.Left <= blockRight - Math.Min(8, blockWidth * 0.10));
    }

    private static bool HasLargeFontChange(IReadOnlyList<OcrLine> block, OcrLine line)
    {
        var medianHeight = block
            .Select(item => Math.Max(1, item.Height))
            .OrderBy(value => value)
            .ElementAt(block.Count / 2);
        var ratio = Math.Max(medianHeight, Math.Max(1, line.Height)) / Math.Max(1.0, Math.Min(medianHeight, Math.Max(1, line.Height)));
        return ratio >= 1.55;
    }

    private static OcrLayoutBlock MakeLayoutBlock(IReadOnlyList<OcrLine> lines)
    {
        var left = lines.Min(line => line.Left);
        var top = lines.Min(line => line.Top);
        var right = lines.Max(line => line.Left + line.Width);
        var bottom = lines.Max(line => line.Top + line.Height);
        var text = string.Join("\n", lines.OrderBy(line => line.Top).ThenBy(line => line.Left).Select(line => line.Text.Trim()));
        return new OcrLayoutBlock(NormalizeText(text), left, top, right - left, bottom - top);
    }

    private static Dictionary<int, string> SplitNumberedTranslation(string translatedBatch, int expectedLines)
    {
        var result = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(translatedBatch))
        {
            return result;
        }

        var normalized = translatedBatch.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var currentIndex = -1;
        var current = new StringBuilder();
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (TryReadLineMarker(line, out var oneBased, out var rest))
            {
                if (currentIndex >= 0)
                {
                    result[currentIndex] = current.ToString().Trim();
                    current.Clear();
                }

                currentIndex = oneBased - 1;
                current.Append(rest.Trim());
                continue;
            }

            if (currentIndex >= 0)
            {
                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(line);
            }
        }

        if (currentIndex >= 0)
        {
            result[currentIndex] = current.ToString().Trim();
        }

        if (result.Count == 0)
        {
            var fallbackLines = normalized.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
            for (var i = 0; i < Math.Min(expectedLines, fallbackLines.Length); i++)
            {
                result[i] = fallbackLines[i];
            }
        }

        return result;
    }


    private static bool TryReadLineMarker(string line, out int oneBased, out string rest)
    {
        oneBased = 0;
        rest = line;
        var first = line.IndexOf("@@", StringComparison.Ordinal);
        if (first < 0)
        {
            return false;
        }

        var second = line.IndexOf("@@", first + 2, StringComparison.Ordinal);
        if (second <= first + 2)
        {
            return false;
        }

        var marker = line.Substring(first + 2, second - first - 2);
        if (!int.TryParse(marker, out oneBased))
        {
            return false;
        }

        rest = line[(second + 2)..];
        return true;
    }

    private static bool IsJsonLike(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        return trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal);
    }

    private static OcrReadResult ParseOcrResult(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new OcrReadResult(string.Empty, Array.Empty<OcrLine>());
        }

        var json = ExtractJsonObject(output);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var parsed = BuildOcrReadResult(document.RootElement);
                if (!string.IsNullOrWhiteSpace(parsed.Text) || parsed.Lines.Count > 0)
                {
                    return parsed;
                }

                // JSON was detected, but its shape is unknown. Do not pass raw JSON to Chrome or to the overlay.
                return new OcrReadResult(string.Empty, Array.Empty<OcrLine>());
            }
            catch
            {
                // If something that looks like JSON cannot be parsed, do not translate/show the JSON payload as text.
                return new OcrReadResult(string.Empty, Array.Empty<OcrLine>());
            }
        }

        return new OcrReadResult(NormalizeText(output), Array.Empty<OcrLine>());
    }

    private static OcrReadResult BuildOcrReadResult(JsonElement root)
    {
        var text = GetStringPropertyAny(root, "text", "Text", "TEXT", "Текст", "текст", "Tekst");
        var lines = new List<OcrLine>();

        if (TryGetPropertyAny(root, out var linesElement, "lines", "Lines", "LINES", "строки", "Строки", "zeilen", "Zeilen", "linhas")
            && linesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in linesElement.EnumerateArray())
            {
                var lineText = GetStringPropertyAny(item, "text", "Text", "TEXT", "Текст", "текст", "Tekst");
                if (string.IsNullOrWhiteSpace(lineText))
                {
                    continue;
                }

                lines.Add(new OcrLine(
                    NormalizeText(lineText),
                    GetDoublePropertyAny(item, "left", "Left", "LEFT", "лево", "Лево", "слева", "links", "Links", "x", "X"),
                    GetDoublePropertyAny(item, "top", "Top", "TOP", "верх", "Верх", "верхний", "Верхний", "oben", "Oben", "y", "Y"),
                    GetDoublePropertyAny(item, "width", "Width", "WIDTH", "ширина", "Ширина", "breite", "Breite", "w", "W"),
                    GetDoublePropertyAny(item, "height", "Height", "HEIGHT", "высота", "Высота", "hoehe", "Höhe", "height", "h", "H")));
            }
        }

        if (string.IsNullOrWhiteSpace(text) && lines.Count > 0)
        {
            text = string.Join(Environment.NewLine, lines.Select(line => line.Text));
        }

        return new OcrReadResult(NormalizeText(text ?? string.Empty), lines);
    }

    private static string ExtractJsonObject(string output)
    {
        var trimmed = output.TrimStart('\ufeff', ' ', '\t', '\r', '\n').TrimEnd();
        var firstObject = trimmed.IndexOf('{');
        var lastObject = trimmed.LastIndexOf('}');
        if (firstObject >= 0 && lastObject > firstObject)
        {
            return trimmed[firstObject..(lastObject + 1)];
        }

        var firstArray = trimmed.IndexOf('[');
        var lastArray = trimmed.LastIndexOf(']');
        return firstArray >= 0 && lastArray > firstArray ? trimmed[firstArray..(lastArray + 1)] : string.Empty;
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

    private static ScreenTranslationResult BuildResult(string originalText, string translatedText, IReadOnlyList<ScreenTranslationSegment> segments, string targetLanguage, bool cached, bool shortened)
    {
        var hasTranslation = !string.IsNullOrWhiteSpace(translatedText) && !IsJsonLike(translatedText);
        var translatedSegments = segments.Count(segment =>
            !string.IsNullOrWhiteSpace(segment.TranslatedText)
            && !LooksLikeUntranslatedEcho(segment.TranslatedText, segment.OriginalText, targetLanguage));
        var untranslatedSegments = segments.Count(segment =>
            !string.IsNullOrWhiteSpace(segment.TranslatedText)
            && LooksLikeUntranslatedEcho(segment.TranslatedText, segment.OriginalText, targetLanguage));
        var hasUsefulStructuredTranslation = translatedSegments > 0;
        var displayText = hasTranslation ? translatedText : (IsJsonLike(originalText) ? string.Empty : originalText);
        var status = string.IsNullOrWhiteSpace(originalText)
            ? "OCR aktiv, aber kein Text erkannt."
            : hasTranslation
                ? $"Nach {targetLanguage} übersetzt" + (shortened ? " (gekürzt)." : cached ? " (unverändert)." : ".")
                : $"OCR aktiv. Chrome-Übersetzer ist nicht bereit, Originaltext wird angezeigt.";

        return new ScreenTranslationResult
        {
            OriginalText = originalText,
            TranslatedText = displayText,
            Segments = segments,
            Status = BuildTranslationStatus(originalText, targetLanguage, segments, translatedSegments, untranslatedSegments, hasUsefulStructuredTranslation, shortened, cached, status),
            IsAvailable = !string.IsNullOrWhiteSpace(displayText)
        };
    }

    private static string BuildTranslationStatus(
        string originalText,
        string targetLanguage,
        IReadOnlyList<ScreenTranslationSegment> segments,
        int translatedSegments,
        int untranslatedSegments,
        bool hasUsefulStructuredTranslation,
        bool shortened,
        bool cached,
        string fallbackStatus)
    {
        if (string.IsNullOrWhiteSpace(originalText))
        {
            return "OCR aktiv, aber kein Text erkannt.";
        }

        if (segments.Count > 0 && hasUsefulStructuredTranslation && untranslatedSegments > 0)
        {
            return $"Nach {targetLanguage} teilweise übersetzt ({translatedSegments}/{segments.Count} Blöcke).";
        }

        if (segments.Count > 0 && !hasUsefulStructuredTranslation)
        {
            return "OCR aktiv. Chrome-Übersetzer ist nicht bereit, Originaltext wird angezeigt.";
        }

        if (!string.IsNullOrWhiteSpace(fallbackStatus))
        {
            return fallbackStatus
                .Replace("Ã¼", "ü", StringComparison.Ordinal)
                .Replace("Ãœ", "Ü", StringComparison.Ordinal)
                .Replace("Ã¤", "ä", StringComparison.Ordinal)
                .Replace("Ã¶", "ö", StringComparison.Ordinal);
        }

        return $"Nach {targetLanguage} übersetzt" + (shortened ? " (gekürzt)." : cached ? " (unverändert)." : ".");
    }

    private static PreparedTranslationText PrepareTextForChromeTranslation(string originalText)
    {
        if (string.IsNullOrWhiteSpace(originalText))
        {
            return new PreparedTranslationText(originalText, false);
        }

        const int maxVisibleLines = 40;
        const int maxCharacters = 4000;

        var normalizedInput = originalText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var filterNavigation = normalizedInput.Length > 120;

        var lines = normalizedInput
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !line.Contains("reward", StringComparison.OrdinalIgnoreCase))
            .Where(line => !line.Contains("contracted by", StringComparison.OrdinalIgnoreCase))
            .Where(line => !filterNavigation || !IsLikelyNavigationLine(line))
            .ToArray();

        var keptLines = new List<string>();
        var visibleLineCount = 0;
        var previousWasBlank = false;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                if (!previousWasBlank && keptLines.Count > 0)
                {
                    keptLines.Add(string.Empty);
                }

                previousWasBlank = true;
                continue;
            }

            keptLines.Add(line);
            visibleLineCount++;
            previousWasBlank = false;
            if (visibleLineCount >= maxVisibleLines)
            {
                break;
            }
        }

        var text = string.Join(Environment.NewLine, keptLines).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            text = normalizedInput.Trim();
        }

        if (text.Length <= maxCharacters)
        {
            return new PreparedTranslationText(text, !string.Equals(text, originalText, StringComparison.Ordinal));
        }

        return new PreparedTranslationText(text[..maxCharacters], true);
    }

    private static bool IsLikelyNavigationLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (line.Length > 55)
        {
            return false;
        }

        var letters = line.Where(char.IsLetter).ToArray();
        if (letters.Length < 6)
        {
            return false;
        }

        var upperLetters = letters.Count(char.IsUpper);
        return upperLetters >= letters.Length * 0.8;
    }

    private static string NormalizeTargetLanguage(string? targetLanguage)
    {
        return string.IsNullOrWhiteSpace(targetLanguage) ? "de" : targetLanguage.Trim().ToLowerInvariant();
    }

    private sealed record PreparedTranslationText(string Text, bool WasShortened);

    private static async Task<string> RunProcessAsync(string fileName, string arguments, int timeoutMs)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort timeout cleanup.
            }

            return string.Empty;
        }

        var output = await outputTask;
        var error = await errorTask;
        return string.IsNullOrWhiteSpace(output) ? error : output;
    }

    private static string NormalizeText(string text)
    {
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd())
            .ToArray();

        var normalized = new List<string>();
        var previousWasBlank = true;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (!previousWasBlank)
                {
                    normalized.Add(string.Empty);
                }

                previousWasBlank = true;
                continue;
            }

            normalized.Add(line.Trim());
            previousWasBlank = false;
        }

        while (normalized.Count > 0 && normalized[^1].Length == 0)
        {
            normalized.RemoveAt(normalized.Count - 1);
        }

        return string.Join(Environment.NewLine, normalized);
    }

    private sealed record OcrReadResult(string Text, IReadOnlyList<OcrLine> Lines);

    private sealed record OcrLine(string Text, double Left, double Top, double Width, double Height);

    private sealed record OcrLayoutBlock(string Text, double Left, double Top, double Width, double Height);

    private sealed record OcrJsonPayload(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("lines")] IReadOnlyList<OcrJsonLine>? Lines);

    private sealed record OcrJsonLine(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("left")] double Left,
        [property: JsonPropertyName("top")] double Top,
        [property: JsonPropertyName("width")] double Width,
        [property: JsonPropertyName("height")] double Height);

    public void Dispose()
    {
        _chromeTranslation.Dispose();
    }
}
