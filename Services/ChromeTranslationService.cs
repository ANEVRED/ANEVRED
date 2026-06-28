using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
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
    private TcpListener? _hostListener;
    private CancellationTokenSource? _hostCancellation;
    private Task? _hostTask;
    private string? _hostPageUrl;
    private int _debugPort;

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
            if (_debugPort > 0 && await IsDevToolsReadyAsync(_debugPort, cancellationToken))
            {
                return _debugPort;
            }
        }

        StopChrome();
        var profileRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ANEVRED",
            "ChromeTranslatorProfile");
        Directory.CreateDirectory(profileRoot);
        _profileDirectory = Path.Combine(profileRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_profileDirectory);
        _hostPageUrl = EnsureHostPage();
        _debugPort = GetAvailablePort();

        var startInfo = new ProcessStartInfo
        {
            FileName = chromePath,
            Arguments = $"--headless=new --disable-sync --no-first-run --no-default-browser-check --remote-debugging-port={_debugPort} --user-data-dir=\"{_profileDirectory}\" \"{_hostPageUrl}\"",
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
            CleanupProfileDirectory();
            return 0;
        }

        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsDevToolsReadyAsync(_debugPort, cancellationToken))
            {
                _log?.Invoke("Chrome translation runtime started.");
                return _debugPort;
            }

            await Task.Delay(150, cancellationToken);
        }

        _log?.Invoke("Chrome translation failed: DevTools port was not ready.");
        StopChrome();
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

    private async Task<DevToolsTarget?> CreateDevToolsTabAsync(int port, CancellationToken cancellationToken)
    {
        var url = "http://127.0.0.1:" + port + "/json/new?" + Uri.EscapeDataString(_hostPageUrl ?? EnsureHostPage());
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return JsonSerializer.Deserialize<DevToolsTarget>(json);
    }

    private string EnsureHostPage()
    {
        if (_hostListener is not null && !string.IsNullOrWhiteSpace(_hostPageUrl))
        {
            return _hostPageUrl;
        }

        _hostCancellation = new CancellationTokenSource();
        _hostListener = new TcpListener(IPAddress.Loopback, 0);
        _hostListener.Start();
        var port = ((IPEndPoint)_hostListener.LocalEndpoint).Port;
        _hostPageUrl = $"http://127.0.0.1:{port}/";
        _hostTask = RunHostServerAsync(_hostListener, _hostCancellation.Token);
        return _hostPageUrl;
    }

    private static async Task<bool> IsDevToolsReadyAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HttpClient.GetAsync(
                $"http://127.0.0.1:{port}/json/version",
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task RunHostServerAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        const string html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width">
  <title>ANEVRED Chrome Translator Host</title>
</head>
<body></body>
</html>
""";
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n");

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
                await using var stream = client.GetStream();
                await stream.WriteAsync(header, cancellationToken);
                await stream.WriteAsync(body, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                client?.Dispose();
            }
        }
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
        if (!string.IsNullOrWhiteSpace(payload?.SourceLanguage))
        {
            _log?.Invoke($"Chrome translation source detected/selected: {payload.SourceLanguage} -> {targetLanguage}, availability={payload.Availability}.");
        }

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

  async function waitUntilReady(model) {
    if (model && model.ready && typeof model.ready.then === 'function') {
      await model.ready;
    }
    return model;
  }

  async function createWithOptionalMonitor(factory, options) {
    try {
      return await waitUntilReady(await factory.create({
        ...options,
        monitor(monitor) {
          if (monitor && typeof monitor.addEventListener === 'function') {
            monitor.addEventListener('downloadprogress', () => {});
          }
        }
      }));
    } catch {
      return await waitUntilReady(await factory.create(options));
    }
  }

  if ('LanguageDetector' in globalThis) {
    try {
      const detectorAvailability = await LanguageDetector.availability();
      if (detectorAvailability !== 'unavailable') {
        const detector = await createWithOptionalMonitor(LanguageDetector, {});
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

    try {
      const options = { sourceLanguage, targetLanguage };
      const availability = await Translator.availability(options);
      if (availability === 'unavailable') {
        errors.push(`${sourceLanguage}->${targetLanguage}: unavailable`);
        continue;
      }

      const translator = await createWithOptionalMonitor(Translator, options);
      const translated = await translator.translate({{textJson}});
      if (typeof translated === 'string' && translated.trim().length > 0) {
        return JSON.stringify({ text: translated, sourceLanguage, availability });
      }
    } catch (error) {
      errors.push(`${sourceLanguage}->${targetLanguage}: ${error && error.message ? error.message : error}`);
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

  async function waitUntilReady(model) {
    if (model && model.ready && typeof model.ready.then === 'function') {
      await model.ready;
    }
    return model;
  }

  async function createWithOptionalMonitor(factory, options) {
    try {
      return await waitUntilReady(await factory.create({
        ...options,
        monitor(monitor) {
          if (monitor && typeof monitor.addEventListener === 'function') {
            monitor.addEventListener('downloadprogress', () => {});
          }
        }
      }));
    } catch {
      return await waitUntilReady(await factory.create(options));
    }
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

  const translator = await createWithOptionalMonitor(Translator, { sourceLanguage, targetLanguage });
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
                process.WaitForExit(3000);
            }
        }
        catch
        {
            // Best-effort Chrome cleanup.
        }
        finally
        {
            process.Dispose();
            CleanupProfileDirectory();
            _debugPort = 0;
        }
    }

    private void CleanupProfileDirectory()
    {
        if (string.IsNullOrWhiteSpace(_profileDirectory))
        {
            return;
        }

        var profileDirectory = _profileDirectory;
        _profileDirectory = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (TryDeleteDirectory(profileDirectory))
            {
                TryDeleteEmptyParentDirectory(profileDirectory);
                return;
            }

            Thread.Sleep(150);
        }

        _log?.Invoke("Chrome translation profile cleanup deferred to the data retention cleanup.");
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return true;
            }

            Directory.Delete(path, recursive: true);
            return true;
        }
        catch
        {
            // Best-effort cache cleanup. Chrome may keep files locked briefly.
            return false;
        }
    }

    private static void TryDeleteEmptyParentDirectory(string path)
    {
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent)
                && Directory.Exists(parent)
                && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch
        {
            // The retention cleanup handles any empty or stale parent later.
        }
    }

    public void Dispose()
    {
        StopChrome();
        _hostCancellation?.Cancel();
        _hostListener?.Stop();
        _hostCancellation?.Dispose();
        _hostCancellation = null;
        _hostListener = null;
        _hostTask = null;
        _hostPageUrl = null;
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
        [property: JsonPropertyName("sourceLanguage")] string? SourceLanguage,
        [property: JsonPropertyName("availability")] string? Availability,
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
