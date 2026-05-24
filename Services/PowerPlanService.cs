using System.Diagnostics;

namespace ZestResourceOptimizer.Services;

public sealed class PowerPlanService
{
    private readonly LocalizationService _localizer;
    private readonly Action<string, string> _log;

    public PowerPlanService(LocalizationService localizer, Action<string, string> log)
    {
        _localizer = localizer;
        _log = log;
    }

    public async Task OptimizeCurrentPlanAsync()
    {
        var commands = new[]
        {
            "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100",
            "/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100",
            "/setactive SCHEME_CURRENT"
        };

        foreach (var command in commands)
        {
            var result = await RunPowerCfgAsync(command);
            if (result != 0)
            {
                _log("Warn", _localizer.Format("LogPowerPlanFailed", command));
                return;
            }
        }

        _log("Info", _localizer["LogPowerPlanOptimized"]);
    }

    private static async Task<int> RunPowerCfgAsync(string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
