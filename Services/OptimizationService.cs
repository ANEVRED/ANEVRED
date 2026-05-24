using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ZestResourceOptimizer.Models;

namespace ZestResourceOptimizer.Services;

public sealed class OptimizationService
{
    private static readonly string[] KillSwitchCandidates =
    [
        "steamwebhelper",
        "epicwebhelper",
        "overwolf",
        "msedge",
        "msedgewebview2",
        "widgets",
        "yourphone",
        "phoneexperiencehost",
        "teams",
        "msteams",
        "onedrive",
        "gamebar",
        "gamebarftserver",
        "xbox",
        "xboxappservices",
        "winstore.app"
    ];

    private readonly AppSettings _settings;
    private readonly ProcessProtectionService _protectionService;
    private readonly LocalizationService _localizer;
    private readonly Action<string, string> _log;
    private readonly Dictionary<int, ProcessPriorityClass> _changedPriorities = [];
    private readonly int _ownProcessId = Environment.ProcessId;

    public OptimizationService(
        AppSettings settings,
        ProcessProtectionService protectionService,
        LocalizationService localizer,
        Action<string, string> log)
    {
        _settings = settings;
        _protectionService = protectionService;
        _localizer = localizer;
        _log = log;
    }

    public void OptimizeMemory(IReadOnlyList<ProcessSnapshot> processes, bool userRequested = false)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var checkedCount = processes.Count;
        var classified = processes
            .Select(process => new
            {
                Process = process,
                Reason = ClassifyMemoryCandidate(process, userRequested)
            })
            .ToList();

        var protectedSkipped = classified.Count(item => item.Reason is MemorySkipReason.OwnProcess or MemorySkipReason.ProtectedOrSystem);
        var activeSkipped = classified.Count(item => item.Reason == MemorySkipReason.ActiveForeground);
        var smallSkipped = classified.Count(item => item.Reason == MemorySkipReason.BelowMemoryFloor);
        var busySkipped = classified.Count(item => item.Reason == MemorySkipReason.BusyForeground);

        var eligible = classified
            .Where(item => item.Reason == MemorySkipReason.Eligible)
            .Select(item => item.Process)
            .OrderByDescending(process => process.MemoryMb)
            .ToList();

        var candidates = eligible
            .Take(ProfileMemoryLimit(userRequested))
            .ToList();

        var apiSuccess = 0;
        var measurableRelieved = 0;
        var denied = 0;
        var freedMb = 0d;

        foreach (var candidate in candidates)
        {
            var handle = IntPtr.Zero;
            try
            {
                handle = NativeMethods.OpenProcess(
                    NativeMethods.ProcessSetQuota | NativeMethods.ProcessQueryInformation,
                    false,
                    candidate.Id);

                if (handle == IntPtr.Zero || !NativeMethods.EmptyWorkingSet(handle))
                {
                    denied++;
                    continue;
                }

                apiSuccess++;
                var afterMb = TryReadWorkingSetMb(candidate.Id);
                if (afterMb >= 0 && candidate.MemoryMb > afterMb)
                {
                    var delta = candidate.MemoryMb - afterMb;
                    if (delta >= 1)
                    {
                        measurableRelieved++;
                        freedMb += delta;
                    }
                }
            }
            catch
            {
                denied++;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    _ = NativeMethods.CloseHandle(handle);
                }
            }
        }

        _log("Info", _localizer.Format(
            "LogMemoryOptimizedDetailed",
            checkedCount,
            eligible.Count,
            candidates.Count,
            apiSuccess,
            measurableRelieved,
            freedMb,
            denied,
            protectedSkipped,
            activeSkipped,
            smallSkipped,
            busySkipped));
    }

    public bool OptimizeCpu(IReadOnlyList<ProcessSnapshot> processes, bool userRequested = false)
    {
        var checkedCount = processes.Count;
        var protectedSkipped = processes.Count(process =>
            process.Id == _ownProcessId
            || process.IsProtected
            || process.IsCritical
            || _protectionService.IsProtectedProcessName(process.Name));

        var eligible = processes
            .Select(process => ClassifyCpuCandidate(process, userRequested))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .OrderByDescending(candidate => candidate.Process.CpuPercent)
            .ToList();

        var candidates = eligible
            .Take(ProfileCpuLimit())
            .ToList();

        if (candidates.Count == 0)
        {
            if (userRequested)
            {
                _log("Info", _localizer.Format(
                    "LogCpuNoCandidates",
                    checkedCount,
                    protectedSkipped));
            }

            return false;
        }

        var changed = 0;
        var alreadyLow = 0;
        var denied = 0;

        foreach (var candidate in candidates)
        {
            try
            {
                using var process = Process.GetProcessById(candidate.Process.Id);
                var currentPriority = process.PriorityClass;
                var newPriority = candidate.SoftOnly
                    ? LowerPriorityOneStep(currentPriority)
                    : ProcessPriorityClass.BelowNormal;
                if (newPriority == currentPriority)
                {
                    alreadyLow++;
                    continue;
                }

                _changedPriorities.TryAdd(candidate.Process.Id, currentPriority);
                process.PriorityClass = newPriority;
                changed++;
            }
            catch (Win32Exception)
            {
                denied++;
            }
            catch (UnauthorizedAccessException)
            {
                denied++;
            }
            catch (InvalidOperationException)
            {
                denied++;
            }
        }

        _log("Info", _localizer.Format(
            "LogCpuOptimizedDetailed",
            checkedCount,
            eligible.Count,
            candidates.Count,
            changed,
            alreadyLow,
            denied,
            protectedSkipped));

        return changed > 0 || alreadyLow > 0 || denied > 0;
    }

    public void OptimizeVramExperimental(IReadOnlyList<ProcessSnapshot> processes)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var checkedCount = processes.Count;
        var protectedSkipped = processes.Count(process =>
            process.Id == _ownProcessId
            || (!process.IsStarCitizen && process.IsProtected)
            || process.IsCritical
            || (!process.IsStarCitizen && _protectionService.IsProtectedProcessName(process.Name)));

        var eligible = processes
            .Where(IsVramReliefCandidate)
            .OrderByDescending(process => process.VramMb)
            .ThenByDescending(process => process.MemoryMb)
            .ToList();

        var candidates = eligible
            .Take(ProfileVramReliefLimit())
            .ToList();

        var apiSuccess = 0;
        var denied = 0;

        foreach (var candidate in candidates)
        {
            var handle = IntPtr.Zero;
            try
            {
                handle = NativeMethods.OpenProcess(
                    NativeMethods.ProcessSetQuota | NativeMethods.ProcessQueryInformation,
                    false,
                    candidate.Id);

                if (handle == IntPtr.Zero || !NativeMethods.EmptyWorkingSet(handle))
                {
                    denied++;
                    continue;
                }

                apiSuccess++;
            }
            catch
            {
                denied++;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    _ = NativeMethods.CloseHandle(handle);
                }
            }
        }

        _log("Info", _localizer.Format(
            "LogVramReliefExperimental",
            checkedCount,
            eligible.Count,
            candidates.Count,
            apiSuccess,
            denied,
            protectedSkipped));
    }

    public void RunGamingKillSwitch(IReadOnlyList<ProcessSnapshot> processes)
    {
        var eligible = processes
            .Where(IsKillSwitchCandidate)
            .OrderByDescending(process => process.CommitMb)
            .ThenByDescending(process => process.MemoryMb)
            .Take(24)
            .ToList();

        var closeRequested = 0;
        var killed = 0;
        var denied = 0;
        var names = new List<string>();

        foreach (var candidate in eligible)
        {
            try
            {
                using var process = Process.GetProcessById(candidate.Id);
                names.Add(candidate.Name);

                if (process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow())
                {
                    closeRequested++;
                    continue;
                }

                process.Kill(entireProcessTree: true);
                killed++;
            }
            catch (Win32Exception)
            {
                denied++;
            }
            catch (UnauthorizedAccessException)
            {
                denied++;
            }
            catch (InvalidOperationException)
            {
                denied++;
            }
        }

        _log("Warn", _localizer.Format(
            "LogKillSwitchDetailed",
            processes.Count,
            eligible.Count,
            closeRequested,
            killed,
            denied,
            names.Count == 0 ? "-" : string.Join(", ", names.Distinct().Take(8))));
    }

    public void ResetChangedPriorities()
    {
        var restored = 0;
        foreach (var (pid, priority) in _changedPriorities.ToList())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.PriorityClass = priority;
                restored++;
                _changedPriorities.Remove(pid);
            }
            catch
            {
                _changedPriorities.Remove(pid);
            }
        }

        _log("Info", _localizer.Format("LogPrioritiesReset", restored));
    }

    private MemorySkipReason ClassifyMemoryCandidate(ProcessSnapshot process, bool userRequested)
    {
        if (process.Id == _ownProcessId)
        {
            return MemorySkipReason.OwnProcess;
        }

        if (process.IsProtected || process.IsCritical || _protectionService.IsProtectedProcessName(process.Name))
        {
            return MemorySkipReason.ProtectedOrSystem;
        }

        if (process.MemoryMb < ProfileMemoryFloorMb(userRequested))
        {
            return MemorySkipReason.BelowMemoryFloor;
        }

        if (!process.IsBackground && !userRequested)
        {
            return MemorySkipReason.ActiveForeground;
        }

        if (!process.IsBackground && userRequested && process.CpuPercent > 1.5)
        {
            return MemorySkipReason.BusyForeground;
        }

        return MemorySkipReason.Eligible;
    }

    private bool IsCpuCandidate(ProcessSnapshot process, bool userRequested)
    {
        return ClassifyCpuCandidate(process, userRequested) is not null;
    }

    private CpuCandidate? ClassifyCpuCandidate(ProcessSnapshot process, bool userRequested)
    {
        if (process.Id == _ownProcessId
            || process.IsProtected
            || process.IsCritical
            || _protectionService.IsProtectedProcessName(process.Name)
            || process.Priority.Equals("Idle", StringComparison.OrdinalIgnoreCase)
            || process.Priority.Equals("BelowNormal", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (process.IsBackground && process.CpuPercent >= ProfileCpuFloorPercent())
        {
            return new CpuCandidate(process, SoftOnly: false);
        }

        if (userRequested && (process.CpuPercent >= 0.5 || process.MemoryMb >= 256))
        {
            return new CpuCandidate(process, SoftOnly: true);
        }

        return null;
    }

    private static ProcessPriorityClass LowerPriorityOneStep(ProcessPriorityClass priority)
    {
        return priority switch
        {
            ProcessPriorityClass.RealTime => ProcessPriorityClass.High,
            ProcessPriorityClass.High => ProcessPriorityClass.AboveNormal,
            ProcessPriorityClass.AboveNormal => ProcessPriorityClass.Normal,
            ProcessPriorityClass.Normal => ProcessPriorityClass.BelowNormal,
            _ => priority
        };
    }

    private bool IsVramReliefCandidate(ProcessSnapshot process)
    {
        if (process.Id == _ownProcessId || process.IsCritical)
        {
            return false;
        }

        if (process.IsStarCitizen)
        {
            return true;
        }

        return !process.IsProtected
            && process.IsBackground
            && (process.VramMb >= 64 || process.MemoryMb >= 180)
            && !_protectionService.IsProtectedProcessName(process.Name)
            && IsCommonGpuBackgroundProcess(process.Name);
    }

    private bool IsKillSwitchCandidate(ProcessSnapshot process)
    {
        if (process.Id == _ownProcessId
            || process.IsProtected
            || process.IsCritical
            || process.IsStarCitizen
            || _protectionService.IsProtectedProcessName(process.Name))
        {
            return false;
        }

        var normalized = Path.GetFileNameWithoutExtension(process.Name);
        return KillSwitchCandidates.Any(candidate =>
            normalized.Equals(candidate, StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCommonGpuBackgroundProcess(string processName)
    {
        var normalized = Path.GetFileNameWithoutExtension(processName).ToLowerInvariant();
        return normalized is
            "chrome"
            or "msedge"
            or "brave"
            or "opera"
            or "firefox"
            or "discord"
            or "steamwebhelper"
            or "epicwebhelper"
            or "overwolf"
            or "obs64"
            or "obs32"
            or "wallpaper32"
            or "wallpaper64"
            or "teams"
            or "slack"
            or "spotify"
            or "telegram"
            or "whatsapp"
            or "medal";
    }

    private int ProfileMemoryLimit(bool userRequested)
    {
        if (userRequested)
        {
            return _settings.Profile switch
            {
                AppProfile.Gaming => 14,
                AppProfile.Silent => 8,
                _ => 12
            };
        }

        return _settings.Profile switch
        {
            AppProfile.Gaming => 10,
            AppProfile.Silent => 3,
            _ => 6
        };
    }

    private int ProfileCpuLimit()
    {
        return _settings.Profile switch
        {
            AppProfile.Gaming => 8,
            AppProfile.Silent => 2,
            _ => 5
        };
    }

    private int ProfileVramReliefLimit()
    {
        return _settings.Profile switch
        {
            AppProfile.Gaming => 8,
            AppProfile.Silent => 3,
            _ => 5
        };
    }

    private double ProfileMemoryFloorMb(bool userRequested)
    {
        if (userRequested)
        {
            return _settings.Profile switch
            {
                AppProfile.Gaming => 128,
                AppProfile.Silent => 128,
                _ => 160
            };
        }

        return _settings.Profile switch
        {
            AppProfile.Gaming => 180,
            AppProfile.Silent => 500,
            _ => 300
        };
    }

    private double ProfileCpuFloorPercent()
    {
        return _settings.Profile switch
        {
            AppProfile.Gaming => 4,
            AppProfile.Silent => 12,
            _ => 8
        };
    }

    private static double TryReadWorkingSetMb(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Refresh();
            return process.WorkingSet64 / 1024d / 1024d;
        }
        catch
        {
            return -1;
        }
    }

    private enum MemorySkipReason
    {
        Eligible,
        OwnProcess,
        ProtectedOrSystem,
        ActiveForeground,
        BusyForeground,
        BelowMemoryFloor
    }

    private sealed record CpuCandidate(ProcessSnapshot Process, bool SoftOnly);
}
