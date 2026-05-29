using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ANEVRED.Services;

public sealed class ChromeTranslationService : IDisposable
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private readonly Action<string>? _log;
    private Process? _chromeProcess;
    private string? _profileDirectory;

    public ChromeTranslationService(Action<string>? log = null)
    {
        _log = log;
    }

    public async Task<string> TranslateAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var chromePath = FindChromeExecutable();
        if (chromePath is null)
        {
            _log?.Invoke("Chrome translation failed: chrome.exe was not found.");
            return string.Empty;
        }

        var port = await EnsureChromeAsync(chromePath, cancellationToken);
        if (port <= 0)
        {
            return string.Empty;
        }

        var tab = await CreateDevToolsTabAsync(port, cancellationToken);
        if (tab?.WebSocketDebuggerUrl is null)
        {
            _log?.Invoke("Chrome translation failed: DevTools tab could not be opened.");
            return string.Empty;
        }

        var normalizedTarget = NormalizeTargetLanguage(targetLanguage);
        var translated = await EvaluateTranslationAsync(tab.WebSocketDebuggerUrl, text, normalizedTarget, cancellationToken);
        if (!string.IsNullOrWhiteSpace(translated))
        {
            _log?.Invoke($"Chrome translation finished: chars={translated.Length}.");
        }

        return translated;
    }

    public async Task<bool> CheckAsync(string targetLanguage, CancellationToken cancellationToken = default)
    {
        var normalizedTarget = NormalizeTargetLanguage(targetLanguage);
        var chromePath = FindChromeExecutable();
        if (chromePath is null)
        {
            _log?.Invoke("Chrome self-check failed: chrome.exe was not found.");
            return false;
        }

        _log?.Invoke("Chrome self-check: found " + chromePath);
        var version = GetChromeVersion(chromePath);
        if (!string.IsNullOrWhiteSpace(version))
        {
            _log?.Invoke("Chrome self-check: version " + version);
        }

        var port = await EnsureChromeAsync(chromePath, cancellationToken);
        if (port <= 0)
        {
            _log?.Invoke("Chrome self-check failed: Chrome DevTools endpoint was not ready.");
            return false;
        }

        _log?.Invoke("Chrome self-check: DevTools endpoint ready.");
        var tab = await CreateDevToolsTabAsync(port, cancellationToken);
        if (tab?.WebSocketDebuggerUrl is null)
        {
            _log?.Invoke("Chrome self-check failed: DevTools tab could not be opened.");
            return false;
        }

        var payload = await EvaluateDiagnosticAsync(tab.WebSocketDebuggerUrl, normalizedTarget, cancellationToken);
        if (payload is null)
        {
            _log?.Invoke("Chrome self-check failed: no diagnostic response from Chrome.");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(payload.Error))
        {
            _log?.Invoke("Chrome self-check failed: " + payload.Error);
            return false;
        }

        _log?.Invoke($"Chrome self-check: secureContext={payload.IsSecureContext}, Translator API present={payload.HasTranslator}, en->de={payload.EnDeAvailability}, en->ru={payload.EnRuAvailability}, target={payload.TargetAvailability}, source={payload.SourceLanguage}.");
        if (!string.IsNullOrWhiteSpace(payload.Sample))
        {
            _log?.Invoke("Chrome self-check sample: " + payload.Sample);
        }

        return payload.HasTranslator
            && !string.Equals(payload.TargetAvailability, "unavailable", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(payload.Sample);
    }

    private async Task<int> EnsureChromeAsync(string chromePath, CancellationToken cancellationToken)
    {
        if (_chromeProcess is { HasExited: false } && !string.IsNullOrWhiteSpace(_profileDirectory))
        {
            var existingPort = await ReadDevToolsPortAsync(_profileDirectory, cancellationToken);
            if (existingPort > 0)
            {
                return existingPort;
            }
        }

        StopChrome();
        _profileDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ANEVRED",
            "ChromeTranslatorProfile");
        Directory.CreateDirectory(_profileDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = chromePath,
            Arguments = $"--headless=new --disable-gpu --no-first-run --no-default-browser-check --remote-debugging-port=0 --user-data-dir=\"{_profileDirectory}\" about:blank",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            _chromeProcess = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            _log?.Invoke("Chrome translation failed: Chrome could not start: " + ex.Message);
            return 0;
        }

        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var port = await ReadDevToolsPortAsync(_profileDirectory, cancellationToken);
            if (port > 0)
            {
                _log?.Invoke("Chrome translation runtime started.");
                return port;
            }

            await Task.Delay(150, cancellationToken);
        }

        _log?.Invoke("Chrome translation failed: DevTools port was not ready.");
        return 0;
    }

    private static async Task<int> ReadDevToolsPortAsync(string profileDirectory, CancellationToken cancellationToken)
    {
        var activePortFile = Path.Combine(profileDirectory, "DevToolsActivePort");
        if (!File.Exists(activePortFile))
        {
            return 0;
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(activePortFile, cancellationToken);
            return lines.Length > 0 && int.TryParse(lines[0], out var port) ? port : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<DevToolsTarget?> CreateDevToolsTabAsync(int port, CancellationToken cancellationToken)
    {
        var url = "http://127.0.0.1:" + port + "/json/new?" + Uri.EscapeDataString(EnsureTranslatorHostPage());
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return JsonSerializer.Deserialize<DevToolsTarget>(json);
    }

    private static string EnsureTranslatorHostPage()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ANEVRED",
            "ChromeTranslatorProfile");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "translator-host.html");
        if (!File.Exists(path))
        {
            File.WriteAllText(
                path,
                """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>ANEVRED Chrome Translator Host</title>
</head>
<body></body>
</html>
""",
                Encoding.UTF8);
        }

        return new Uri(path).AbsoluteUri;
    }

    private async Task<string> EvaluateTranslationAsync(
        string webSocketUrl,
        string text,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var expression = BuildTranslationExpression(text, targetLanguage);
        var value = await EvaluateJsonStringAsync(webSocketUrl, expression, "Chrome translation", cancellationToken);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var payload = JsonSerializer.Deserialize<ChromeTranslationPayload>(value);
        if (!string.IsNullOrWhiteSpace(payload?.Error))
        {
            _log?.Invoke("Chrome translation failed: " + payload.Error);
            return string.Empty;
        }

        return payload?.Text?.Trim() ?? string.Empty;
    }

    private async Task<ChromeDiagnosticPayload?> EvaluateDiagnosticAsync(
        string webSocketUrl,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var expression = BuildDiagnosticExpression(targetLanguage);
        var value = await EvaluateJsonStringAsync(webSocketUrl, expression, "Chrome self-check", cancellationToken);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<ChromeDiagnosticPayload>(value);
    }

    private async Task<string> EvaluateJsonStringAsync(
        string webSocketUrl,
        string expression,
        string operationName,
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(webSocketUrl), cancellationToken);

        var request = JsonSerializer.Serialize(new DevToolsRequest(
            1,
            "Runtime.evaluate",
            new DevToolsEvaluateParams(expression, true, true, true)));
        await SendStringAsync(socket, request, cancellationToken);

        while (socket.State == WebSocketState.Open)
        {
            var responseJson = await ReceiveStringAsync(socket, cancellationToken);
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(responseJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var idElement) || idElement.GetInt32() != 1)
                {
                    continue;
                }

                if (root.TryGetProperty("exceptionDetails", out var exceptionDetails))
                {
                    var text = exceptionDetails.TryGetProperty("text", out var textElement)
                        ? textElement.GetString()
                        : exceptionDetails.GetRawText();
                    _log?.Invoke(operationName + " failed: " + text);
                    return string.Empty;
                }

                if (!root.TryGetProperty("result", out var result) ||
                    !result.TryGetProperty("result", out var remoteObject))
                {
                    return string.Empty;
                }

                if (remoteObject.TryGetProperty("value", out var value))
                {
                    return value.ValueKind switch
                    {
                        JsonValueKind.String => value.GetString() ?? string.Empty,
                        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                        JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.Number => value.GetRawText(),
                        _ => string.Empty
                    };
                }

                if (remoteObject.TryGetProperty("description", out var description))
                {
                    return description.GetString() ?? string.Empty;
                }

                return string.Empty;
            }
            catch (JsonException ex)
            {
                _log?.Invoke(operationName + " failed: invalid DevTools JSON response: " + ex.Message);
                return string.Empty;
            }
        }

        return string.Empty;
    }

    private static string BuildTranslationExpression(string text, string targetLanguage)
    {
        var textJson = JsonSerializer.Serialize(text);
        var targetJson = JsonSerializer.Serialize(targetLanguage);
        var sourceLanguagesJson = JsonSerializer.Serialize(GetSourceLanguageCandidates(targetLanguage));
        return $$"""
(async () => {
  if (!('Translator' in globalThis)) {
    return JSON.stringify({ error: 'Chrome Translator API is not available in this Chrome installation.' });
  }

  const targetLanguage = {{targetJson}};
  const fallbackSourceLanguages = {{sourceLanguagesJson}};
  let sourceLanguages = fallbackSourceLanguages;
  const errors = [];

  if ('LanguageDetector' in globalThis) {
    try {
      const detectorAvailability = await LanguageDetector.availability();
      if (detectorAvailability !== 'unavailable') {
        const detector = await LanguageDetector.create();
        const detections = await detector.detect({{textJson}});
        const detectedLanguages = (Array.isArray(detections) ? detections : [])
          .map(item => item.detectedLanguage || item.language || item)
          .filter(language => typeof language === 'string' && language.trim().length > 0);
        sourceLanguages = [...new Set([...detectedLanguages, ...fallbackSourceLanguages])];
      }
    } catch {
      sourceLanguages = fallbackSourceLanguages;
    }
  }

  for (const sourceLanguage of sourceLanguages) {
    if (sourceLanguage === targetLanguage) {
      continue;
    }

    const availability = await Translator.availability({ sourceLanguage, targetLanguage });
    if (availability === 'unavailable') {
      errors.push(`${sourceLanguage}->${targetLanguage}: unavailable`);
      continue;
    }

    const translator = await Translator.create({ sourceLanguage, targetLanguage });
    const translated = await translator.translate({{textJson}});
    if (typeof translated === 'string' && translated.trim().length > 0) {
      return JSON.stringify({ text: translated, sourceLanguage });
    }
  }

  return JSON.stringify({ error: `Chrome cannot translate to ${targetLanguage}. Tried: ${errors.join(', ')}` });
})()
""";
    }

    private static string BuildDiagnosticExpression(string targetLanguage)
    {
        var targetJson = JsonSerializer.Serialize(targetLanguage);
        return $$"""
(async () => {
  const targetLanguage = {{targetJson}};
  const result = {
    isSecureContext: globalThis.isSecureContext === true,
    hasTranslator: false,
    targetAvailability: 'not_checked',
    enDeAvailability: 'not_checked',
    enRuAvailability: 'not_checked',
    sample: '',
    error: ''
  };

  if (!('Translator' in globalThis)) {
    result.error = 'Chrome Translator API is not available in this Chrome installation.';
    return JSON.stringify(result);
  }

  result.hasTranslator = true;
  const sourceLanguages = {{JsonSerializer.Serialize(GetSourceLanguageCandidates(targetLanguage))}};
  const sourceLanguage = sourceLanguages.find(language => language !== targetLanguage) || 'en';
  result.sourceLanguage = sourceLanguage;
  result.enDeAvailability = await Translator.availability({ sourceLanguage: 'en', targetLanguage: 'de' });
  result.enRuAvailability = await Translator.availability({ sourceLanguage: 'en', targetLanguage: 'ru' });
  result.targetAvailability = await Translator.availability({ sourceLanguage, targetLanguage });

  if (result.targetAvailability === 'unavailable') {
    result.error = `Chrome cannot translate ${sourceLanguage} to ${targetLanguage}.`;
    return JSON.stringify(result);
  }

  const translator = await Translator.create({ sourceLanguage, targetLanguage });
  result.sample = await translator.translate('Hello pilot. Your mission is ready.');
  return JSON.stringify(result);
})()
""";
    }

    private static async Task SendStringAsync(ClientWebSocket socket, string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<string> ReceiveStringAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return string.Empty;
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? FindChromeExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string GetChromeVersion(string chromePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(chromePath);
            return info.ProductVersion ?? info.FileVersion ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeTargetLanguage(string targetLanguage)
    {
        return targetLanguage.Trim().ToLowerInvariant() switch
        {
            "de" or "de-de" => "de",
            "ru" or "ru-ru" => "ru",
            "en" or "en-us" or "en-gb" => "en",
            "zh" or "zh-cn" or "zh-hans" => "zh-Hans",
            "zh-tw" or "zh-hk" or "zh-hant" => "zh-Hant",
            "ja" or "ja-jp" => "ja",
            "ko" or "ko-kr" => "ko",
            "fr" or "fr-fr" => "fr",
            "es" or "es-es" => "es",
            "it" or "it-it" => "it",
            "pl" or "pl-pl" => "pl",
            "uk" or "uk-ua" => "uk",
            var value when !string.IsNullOrWhiteSpace(value) => value,
            _ => "de"
        };
    }

    private static string[] GetSourceLanguageCandidates(string targetLanguage)
    {
        var normalizedTarget = NormalizeTargetLanguage(targetLanguage);
        var commonSources = new[] { "en", "de", "ru", "zh-Hans", "zh-Hant", "fr", "es", "it", "pl", "uk", "ja", "ko" };
        return commonSources
            .Where(language => !language.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private void StopChrome()
    {
        var process = _chromeProcess;
        _chromeProcess = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort Chrome cleanup.
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        StopChrome();
    }

    private sealed record DevToolsTarget(
        [property: JsonPropertyName("webSocketDebuggerUrl")] string? WebSocketDebuggerUrl);

    private sealed record DevToolsRequest(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] DevToolsEvaluateParams Params);

    private sealed record DevToolsEvaluateParams(
        [property: JsonPropertyName("expression")] string Expression,
        [property: JsonPropertyName("awaitPromise")] bool AwaitPromise,
        [property: JsonPropertyName("returnByValue")] bool ReturnByValue,
        [property: JsonPropertyName("userGesture")] bool UserGesture);

    private sealed record DevToolsResponse(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("result")] DevToolsResponseResult? Result,
        [property: JsonPropertyName("exceptionDetails")] DevToolsExceptionDetails? ExceptionDetails);

    private sealed record DevToolsResponseResult(
        [property: JsonPropertyName("result")] DevToolsRemoteObject? Result);

    private sealed record DevToolsRemoteObject(
        [property: JsonPropertyName("value")] string? Value);

    private sealed record DevToolsExceptionDetails(
        [property: JsonPropertyName("text")] string Text);

    private sealed record ChromeTranslationPayload(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("error")] string? Error);

    [SuppressMessage("Performance", "CA1812", Justification = "Deserialized from Chrome DevTools JSON.")]
    private sealed record ChromeDiagnosticPayload(
        [property: JsonPropertyName("isSecureContext")] bool IsSecureContext,
        [property: JsonPropertyName("hasTranslator")] bool HasTranslator,
        [property: JsonPropertyName("targetAvailability")] string TargetAvailability,
        [property: JsonPropertyName("enDeAvailability")] string EnDeAvailability,
        [property: JsonPropertyName("enRuAvailability")] string EnRuAvailability,
        [property: JsonPropertyName("sourceLanguage")] string? SourceLanguage,
        [property: JsonPropertyName("sample")] string? Sample,
        [property: JsonPropertyName("error")] string? Error);
}
