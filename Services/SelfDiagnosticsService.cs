using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ANEVRED.Models;

namespace ANEVRED.Services;

/// <summary>Bounded local measurements of this application only; no forced collection or timer.</summary>
public sealed class SelfDiagnosticsService : IDisposable
{
    public const int MaxSamples = 120;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Queue<SelfDiagnosticSample> _samples = new(MaxSamples);
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly Func<TimeSpan> _elapsed;
    private double? _initialPrivateMemoryMb;
    private TimeSpan _previousCpu;
    private TimeSpan _previousElapsed;
    private TimeSpan _lastSampleElapsed = TimeSpan.FromSeconds(-5);

    public SelfDiagnosticsService(Func<TimeSpan>? elapsed = null)
    {
        _elapsed = elapsed ?? (() => _uptime.Elapsed);
    }

    public TimeSpan Uptime => _elapsed();
    public SelfDiagnosticSample? Latest { get; private set; }
    public double MemoryChangeMb => Latest is null || _initialPrivateMemoryMb is null
        ? 0 : Latest.PrivateMemoryMb - _initialPrivateMemoryMb.Value;
    public int SampleCount => _samples.Count;

    public bool TrySample()
    {
        var elapsed = _elapsed();
        if (elapsed - _lastSampleElapsed < TimeSpan.FromSeconds(5)) return false;
        try
        {
            _process.Refresh();
            var cpu = _process.TotalProcessorTime;
            var seconds = (elapsed - _previousElapsed).TotalSeconds;
            var usage = Latest is null || seconds <= 0 ? 0
                : Math.Clamp((cpu - _previousCpu).TotalSeconds / seconds / Environment.ProcessorCount * 100, 0, 100);
            Latest = new SelfDiagnosticSample(DateTime.UtcNow,
                _process.PrivateMemorySize64 / 1048576d, _process.WorkingSet64 / 1048576d,
                GC.GetTotalMemory(false) / 1048576d, usage, _process.HandleCount, _process.Threads.Count);
            _initialPrivateMemoryMb ??= Latest.PrivateMemoryMb;
            if (_samples.Count == MaxSamples) _samples.Dequeue();
            _samples.Enqueue(Latest);
            _previousCpu = cpu;
            _previousElapsed = elapsed;
            return true;
        }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        finally { _lastSampleElapsed = elapsed; }
    }

    public void Export(string path)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("AppVersion;TimeUtc;PrivateMemoryMB;WorkingSetMB;ManagedMemoryMB;CpuPercent;Handles;Threads");
        foreach (var sample in _samples)
        {
            writer.WriteLine(string.Join(";", typeof(SelfDiagnosticsService).Assembly.GetName().Version,
                sample.TimeUtc.ToString("O", CultureInfo.InvariantCulture),
                sample.PrivateMemoryMb.ToString("F2", CultureInfo.InvariantCulture),
                sample.WorkingSetMb.ToString("F2", CultureInfo.InvariantCulture),
                sample.ManagedMemoryMb.ToString("F2", CultureInfo.InvariantCulture),
                sample.CpuPercent.ToString("F2", CultureInfo.InvariantCulture), sample.Handles, sample.Threads));
        }
    }

    public void Dispose() => _process.Dispose();
}
