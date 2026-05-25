using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ZestResourceOptimizer.Models;

namespace ZestResourceOptimizer.Services;

public sealed class ScreenTranslationService : IDisposable
{
    private readonly Action<string>? _log;
    private string _lastImageHash = string.Empty;
    private string _lastTargetLanguage = string.Empty;
    private string _lastOriginalText = string.Empty;
    private string _lastTranslatedText = string.Empty;
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
            return BuildResult(_lastOriginalText, _lastTranslatedText, targetLanguage, cached: true, shortened: false);
        }

        _lastImageHash = imageHash;
        _lastTargetLanguage = normalizedTargetLanguage;
        _lastOcr = DateTime.UtcNow;
        var imagePath = Path.Combine(Path.GetTempPath(), "anevred-ocr-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            preparedBitmap.Save(imagePath, ImageFormat.Png);
            SaveDebugCapture(preparedBitmap);
            var originalText = await ReadTextWithWindowsOcrAsync(imagePath);
            cancellationToken.ThrowIfCancellationRequested();
            _log?.Invoke($"OCR finished: chars={originalText.Length}");
            _ocrCompleted?.Invoke();
            var preparedText = PrepareTextForChromeTranslation(originalText);
            if (preparedText.WasShortened)
            {
                _log?.Invoke($"Translation text shortened for Chrome translation: {originalText.Length} -> {preparedText.Text.Length} chars.");
            }

            var translatedText = await TranslateTextAsync(preparedText.Text, targetLanguage, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _log?.Invoke($"Translation finished: chars={translatedText.Length}");

            _lastOriginalText = originalText;
            _lastTranslatedText = translatedText;
            return BuildResult(originalText, translatedText, targetLanguage, cached: false, preparedText.WasShortened);
        }
        finally
        {
            try
            {
                File.Delete(imagePath);
            }
            catch
            {
                // Temporary capture cleanup is best-effort.
            }
        }
    }

    private static Bitmap CaptureRegion(Rectangle region)
    {
        var bitmap = new Bitmap(region.Width, region.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private static Bitmap PrepareForOcr(Bitmap source)
    {
        var scale = source.Width < 1400 ? 2 : 1;
        var width = Math.Min(source.Width * scale, 2600);
        var height = Math.Min(source.Height * scale, 2600);
        var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(scaled);
        graphics.Clear(Color.Black);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
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

    private void SaveDebugCapture(Bitmap bitmap)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ANEVRED",
                "OcrDebug");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "last-ocr-capture.png");
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

    private static async Task<string> ReadTextWithWindowsOcrAsync(string imagePath)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Tools", "WindowsOcr.ps1");
        if (!File.Exists(scriptPath))
        {
            scriptPath = Path.Combine(AppContext.BaseDirectory, "WindowsOcr.ps1");
        }

        if (!File.Exists(scriptPath))
        {
            return string.Empty;
        }

        var output = await RunProcessAsync(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -ImagePath \"{imagePath}\" -LanguageTag en-US",
            timeoutMs: 12000);
        return NormalizeText(output);
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

        _log?.Invoke($"Chrome translation request: target={targetLanguage}, chars={originalText.Length}.");
        return await _chromeTranslation.TranslateAsync(originalText, targetLanguage, cancellationToken);
    }

    private static ScreenTranslationResult BuildResult(string originalText, string translatedText, string targetLanguage, bool cached, bool shortened)
    {
        var hasTranslation = !string.IsNullOrWhiteSpace(translatedText);
        var displayText = hasTranslation ? translatedText : originalText;
        var status = string.IsNullOrWhiteSpace(originalText)
            ? "OCR aktiv, aber kein Text erkannt."
            : hasTranslation
                ? $"Nach {targetLanguage} uebersetzt" + (shortened ? " (gekuerzt)." : cached ? " (unveraendert)." : ".")
                : $"OCR aktiv. Chrome-Uebersetzer ist nicht bereit, Originaltext wird angezeigt.";

        return new ScreenTranslationResult
        {
            OriginalText = originalText,
            TranslatedText = displayText,
            Status = status,
            IsAvailable = !string.IsNullOrWhiteSpace(displayText)
        };
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

    public void Dispose()
    {
        _chromeTranslation.Dispose();
    }
}


