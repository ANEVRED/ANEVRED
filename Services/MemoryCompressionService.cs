using System.ComponentModel;
using System.Diagnostics;

namespace ZestResourceOptimizer.Services;

public sealed class MemoryCompressionService
{
    private readonly LocalizationService _localizer;
    private readonly Action<string, string> _log;

    public MemoryCompressionService(LocalizationService localizer, Action<string, string> log)
    {
        _localizer = localizer;
        _log = log;
    }

    public void Disable()
    {
        RunElevated("Disable-MMAgent -MemoryCompression", "LogMemoryCompressionDisableRequested");
    }

    public void Enable()
    {
        RunElevated("Enable-MMAgent -MemoryCompression", "LogMemoryCompressionEnableRequested");
    }

    private void RunElevated(string command, string logKey)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + command + "\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });

            _log("Warn", _localizer[logKey]);
        }
        catch (Win32Exception)
        {
            _log("Warn", _localizer["LogMemoryCompressionAdminCanceled"]);
        }
        catch (Exception ex)
        {
            _log("Warn", _localizer.Format("LogMemoryCompressionFailed", ex.Message));
        }
    }
}
