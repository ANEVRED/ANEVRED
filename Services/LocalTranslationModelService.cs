using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZestResourceOptimizer.Services;

public sealed class LocalTranslationModelService : IDisposable
{
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WorkerResponse>> _pendingRequests = new();
    private TranslationModel? _activeModel;
    private Process? _workerProcess;
    private bool _disposed;

    public LocalTranslationModelService(Action<string>? log = null)
    {
        _log = log;
    }

    public async Task<string> TranslateAsync(string sourceText, string targetLanguage, string engine, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return string.Empty;
        }

        if (targetLanguage.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            return sourceText;
        }

        var requestedRuntime = NormalizeEngine(engine);
        var model = FindModel(targetLanguage, requestedRuntime is "Auto" or "DirectML");
        if (model is null)
        {
            _log?.Invoke($"Local translation model missing for target={targetLanguage}.");
            return string.Empty;
        }

        var runtime = ResolveRuntime(model, engine);
        _log?.Invoke($"Local translation model start: {model.RunnerPath}");
        _log?.Invoke($"Local translation model directory: {model.Directory}");
        _log?.Invoke($"Local translation runtime: requested={runtime.RequestedEngine}, active={runtime.ActiveEngine}.");
        var translated = await RunModelAsync(model, runtime, sourceText, cancellationToken).ConfigureAwait(false);
        _log?.Invoke($"Local translation model finished: chars={translated.Length}");
        return translated;
    }

    public string ExpectedModelDirectory(string targetLanguage)
    {
        return Path.Combine(AppContext.BaseDirectory, "Models", "Translation", "en-" + targetLanguage.ToLowerInvariant());
    }

    public async Task WarmUpAsync(string targetLanguage, string engine, CancellationToken cancellationToken = default)
    {
        if (targetLanguage.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var requestedRuntime = NormalizeEngine(engine);
        var model = FindModel(targetLanguage, requestedRuntime is "Auto" or "DirectML");
        if (model is null)
        {
            _log?.Invoke($"Local translation model missing for target={targetLanguage}.");
            return;
        }

        var runtime = ResolveRuntime(model, engine);
        _log?.Invoke($"Local translation warmup start: target={targetLanguage}, engine={runtime.ActiveEngine}.");
        var warmupText = await RunModelAsync(model, runtime, "Hello.", cancellationToken).ConfigureAwait(false);
        _log?.Invoke($"Local translation warmup finished: chars={warmupText.Length}.");
    }

    public void CancelActiveRequest()
    {
        _log?.Invoke("Local translation cancel requested.");
        RestartWorker();
    }

    private TranslationModel? FindModel(string targetLanguage, bool preferDirectMl)
    {
        var fallbackModel = default(TranslationModel?);
        foreach (var directory in CandidateModelDirectories(targetLanguage))
        {
            if (!IsUsableModelDirectory(directory))
            {
                continue;
            }

            var directMlStatus = InspectDirectMlRuntime(directory);
            _log?.Invoke(
                $"Local translation candidate: path={directory}, directml={directMlStatus.IsAvailable}, directmlDll={directMlStatus.DirectMlDllCount}, onnxDll={directMlStatus.OnnxRuntimeDllCount}, dmlProviderDll={directMlStatus.ProviderDllCount}");
            var runnerCandidates = new[]
            {
                Path.Combine(directory, "anevred-translator.exe"),
                Path.Combine(directory, "translator.exe"),
                Path.Combine(directory, "translator.cmd"),
                Path.Combine(directory, "translator.bat")
            };

            var runner = runnerCandidates.FirstOrDefault(File.Exists);
            if (runner is not null)
            {
                var model = new TranslationModel(directory, runner);
                if (!preferDirectMl || directMlStatus.IsAvailable)
                {
                    _log?.Invoke($"Local translation candidate selected: path={directory}, runner={runner}, directml={directMlStatus.IsAvailable}");
                    return model;
                }

                _log?.Invoke($"Local translation candidate kept as CPU fallback: path={directory}, runner={runner}");
                fallbackModel ??= model;
            }
            else
            {
                _log?.Invoke($"Local translation candidate skipped: no runner in {directory}");
            }
        }

        if (fallbackModel is not null)
        {
            _log?.Invoke($"Local translation fallback model selected: path={fallbackModel.Directory}, runner={fallbackModel.RunnerPath}");
        }

        return fallbackModel;
    }

    private static IEnumerable<string> CandidateModelDirectories(string targetLanguage)
    {
        var relative = Path.Combine("Models", "Translation", "en-" + targetLanguage.ToLowerInvariant());
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative);
            if (Directory.Exists(candidate))
            {
                yield return candidate;
            }

            current = current.Parent;
        }

        var appDirectory = Path.Combine(AppContext.BaseDirectory, relative);
        yield return appDirectory;
    }

    private static bool IsUsableModelDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        var hasNodeRunner = File.Exists(Path.Combine(directory, "translator.mjs"))
            && File.Exists(Path.Combine(directory, "package-lock.json"))
            && File.Exists(Path.Combine(directory, "node_modules", "@xenova", "transformers", "package.json"))
            && Directory.Exists(Path.Combine(directory, "cache"));
        var hasNativeRunner = File.Exists(Path.Combine(directory, "anevred-translator.exe"))
            || File.Exists(Path.Combine(directory, "translator.exe"));

        return hasNodeRunner || hasNativeRunner;
    }

    private async Task<string> RunModelAsync(TranslationModel model, TranslationRuntime runtime, string sourceText, CancellationToken cancellationToken)
    {
        try
        {
            await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log?.Invoke("Local translation request cancelled before start.");
            throw;
        }

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _log?.Invoke("Local translation request cancelled.");
                throw new OperationCanceledException(cancellationToken);
            }

            var worker = EnsureWorker(model, runtime);
            if (worker is null || worker.HasExited)
            {
                return string.Empty;
            }

            var requestId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<WorkerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[requestId] = completion;
            var payload = JsonSerializer.Serialize(new WorkerRequest(requestId, sourceText));

            try
            {
                await worker.StandardInput.WriteLineAsync(payload).ConfigureAwait(false);
                await worker.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _pendingRequests.TryRemove(requestId, out _);
                _log?.Invoke("Local translation worker write failed: " + ex.Message);
                RestartWorker();
                return string.Empty;
            }

            var timeout = runtime.ActiveEngine.Equals("DirectML", StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromSeconds(300)
                : TimeSpan.FromSeconds(35);
            var completed = await Task.WhenAny(completion.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
            if (completed != completion.Task)
            {
                _pendingRequests.TryRemove(requestId, out _);
                if (cancellationToken.IsCancellationRequested)
                {
                    _log?.Invoke("Local translation worker request cancelled.");
                    RestartWorker();
                    throw new OperationCanceledException(cancellationToken);
                }

                _log?.Invoke($"Local translation worker request timed out after {timeout.TotalSeconds:0}s.");
                RestartWorker();
                return string.Empty;
            }

            var response = await completion.Task.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(response.Error))
            {
                _log?.Invoke("Local translation worker error: " + response.Error);
                return string.Empty;
            }

            return string.IsNullOrWhiteSpace(response.Text) ? string.Empty : response.Text.Trim();
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private Process? EnsureWorker(TranslationModel model, TranslationRuntime runtime)
    {
        if (_workerProcess is { HasExited: false }
            && _activeModel?.RunnerPath == model.RunnerPath
            && _activeModel?.Engine == runtime.ActiveEngine)
        {
            return _workerProcess;
        }

        RestartWorker();
        var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = model.RunnerPath,
            Arguments = $"--worker --engine {runtime.ActiveEngine.ToLowerInvariant()}",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        process.StartInfo.Environment["ANEVRED_TRANSLATION_ENGINE"] = runtime.ActiveEngine.Equals("DirectML", StringComparison.OrdinalIgnoreCase)
            ? "directml"
            : "cpu";

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _log?.Invoke("Local translation worker start failed: " + ex.Message);
            process.Dispose();
            return null;
        }

        _activeModel = model with { Engine = runtime.ActiveEngine };
        _workerProcess = process;
        _ = Task.Run(() => ReadWorkerOutputAsync(process));
        _ = Task.Run(() => ReadWorkerErrorsAsync(process));
        _log?.Invoke("Local translation worker started.");
        return process;
    }

    private TranslationRuntime ResolveRuntime(TranslationModel model, string engine)
    {
        var requested = NormalizeEngine(engine);
        var directMlStatus = InspectDirectMlRuntime(model.Directory);
        _log?.Invoke(
            $"Local translation DirectML probe: path={model.Directory}, requested={requested}, available={directMlStatus.IsAvailable}, directmlDll={directMlStatus.DirectMlDllCount}, onnxDll={directMlStatus.OnnxRuntimeDllCount}, dmlProviderDll={directMlStatus.ProviderDllCount}");

        if (requested == "DirectML" && !directMlStatus.IsAvailable)
        {
            _log?.Invoke("DirectML requested, but this local runner has no usable DirectML execution provider. Falling back to CPU.");
            return new TranslationRuntime(requested, "CPU");
        }

        if (requested == "DirectML")
        {
            return new TranslationRuntime(requested, "DirectML");
        }

        if (requested == "Auto")
        {
            if (directMlStatus.IsAvailable)
            {
                return new TranslationRuntime(requested, "DirectML");
            }

            _log?.Invoke("No usable DirectML execution provider found. Auto selected CPU translation.");
            return new TranslationRuntime(requested, "CPU");
        }

        return new TranslationRuntime(requested, "CPU");
    }

    private static bool HasDirectMlProvider(string modelDirectory)
    {
        // ONNX Runtime 1.14.x exposes DirectML through the DirectML-flavored runtime DLLs.
        // Newer builds may also ship a separate provider DLL, so accept either layout.
        return InspectDirectMlRuntime(modelDirectory).IsAvailable || InspectDirectMlRuntime(AppContext.BaseDirectory).IsAvailable;
    }

    private static DirectMlRuntimeStatus InspectDirectMlRuntime(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new DirectMlRuntimeStatus(false, 0, 0, 0);
        }

        var providerDllCount = CountFiles(directory, "onnxruntime_providers_dml.dll");
        var directMlDllCount = CountFiles(directory, "DirectML.dll");
        var onnxRuntimeDllCount = CountFiles(directory, "onnxruntime.dll");
        var available = providerDllCount > 0 || (directMlDllCount > 0 && onnxRuntimeDllCount > 0);
        return new DirectMlRuntimeStatus(available, directMlDllCount, onnxRuntimeDllCount, providerDllCount);
    }

    private static int CountFiles(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).Take(20).Count();
        }
        catch
        {
            return 0;
        }
    }

    private static string NormalizeEngine(string? engine)
    {
        return engine?.Trim().ToLowerInvariant() switch
        {
            "auto" => "Auto",
            "automatic" => "Auto",
            "directml" => "DirectML",
            "gpu" => "DirectML",
            "gpu / directml" => "DirectML",
            "cpu" => "CPU",
            _ => "Auto"
        };
    }

    private async Task ReadWorkerOutputAsync(Process process)
    {
        try
        {
            while (!process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                WorkerResponse? response;
                try
                {
                    response = JsonSerializer.Deserialize<WorkerResponse>(line);
                }
                catch
                {
                    _log?.Invoke("Local translation worker ignored output: " + line);
                    continue;
                }

                if (response is not null && _pendingRequests.TryRemove(response.Id, out var completion))
                {
                    completion.TrySetResult(response);
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke("Local translation worker output failed: " + ex.Message);
        }
    }

    private async Task ReadWorkerErrorsAsync(Process process)
    {
        try
        {
            while (!process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Contains("ready", StringComparison.OrdinalIgnoreCase))
                {
                    _log?.Invoke("Local translation worker ready.");
                    continue;
                }

                if (line.StartsWith("ANEVRED translator ", StringComparison.OrdinalIgnoreCase))
                {
                    _log?.Invoke("Local translation worker: " + line["ANEVRED translator ".Length..]);
                    continue;
                }

                if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("fail", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("warn", StringComparison.OrdinalIgnoreCase))
                {
                    _log?.Invoke("Local translation worker stderr: " + line);
                }
            }
        }
        catch
        {
            // The worker may be closed while the app exits.
        }
    }

    private void RestartWorker()
    {
        foreach (var request in _pendingRequests)
        {
            request.Value.TrySetResult(new WorkerResponse(request.Key, string.Empty, "worker restarted"));
        }

        _pendingRequests.Clear();
        var process = _workerProcess;
        _workerProcess = null;
        _activeModel = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine(JsonSerializer.Serialize(new { command = "exit" }));
                if (!process.WaitForExit(1000))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort worker cleanup.
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RestartWorker();
        _requestLock.Dispose();
    }

    private async Task<string> RunModelOnceAsync(string runnerPath, string sourceText)
    {
        using var process = new Process();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        process.StartInfo = new ProcessStartInfo
        {
            FileName = runnerPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        process.Start();
        await process.StandardInput.WriteAsync(sourceText).ConfigureAwait(false);
        process.StandardInput.Close();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log?.Invoke("Local translation model timed out.");
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

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                _log?.Invoke("Local translation model error: " + error.Split(Environment.NewLine).FirstOrDefault());
            }

            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(output) ? string.Empty : output.Trim();
    }

    private sealed record TranslationModel(string Directory, string RunnerPath, string Engine = "CPU");
    private sealed record TranslationRuntime(string RequestedEngine, string ActiveEngine);
    private sealed record DirectMlRuntimeStatus(bool IsAvailable, int DirectMlDllCount, int OnnxRuntimeDllCount, int ProviderDllCount);
    private sealed record WorkerRequest(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("text")] string Text);

    private sealed record WorkerResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("error")] string Error);
}
