using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZestResourceOptimizer.Models;

namespace ZestResourceOptimizer.Services;

public sealed class LocalLearningService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly AppSettings _settings;
    private readonly LocalizationService _localizer;
    private readonly string _storePath;
    private readonly List<SessionLearningSample> _samples = [];
    private DateTime _lastFlush = DateTime.MinValue;

    public LocalLearningService(AppSettings settings, LocalizationService localizer, string appDataDirectory)
    {
        _settings = settings;
        _localizer = localizer;
        _storePath = Path.Combine(appDataDirectory, "learning-history.json");
        Load();
    }

    public IReadOnlyList<Recommendation> Analyze(SystemSnapshot snapshot)
    {
        if (!_settings.LocalLearningEnabled)
        {
            return
            [
                new Recommendation
                {
                    Title = _localizer["AiLearningDisabled"],
                    Severity = "Low",
                    Explanation = _localizer["AiLearningDisabledText"],
                    ActionText = _localizer["Details"],
                    ActionId = "None",
                    Evidence = _localizer["PrivacyLocalOnly"]
                }
            ];
        }

        AddSample(snapshot);

        var recommendations = new List<Recommendation>();
        AddPressureRecommendations(snapshot, recommendations);
        AddProcessRecommendations(snapshot, recommendations);
        AddStarCitizenRecommendations(snapshot, recommendations);
        AddCoreParkingRecommendations(snapshot, recommendations);

        if (recommendations.Count == 0)
        {
            recommendations.Add(new Recommendation
            {
                Title = _localizer["AiAllClear"],
                Severity = "Low",
                Explanation = _localizer["AiAllClearText"],
                ActionText = _localizer["Details"],
                ActionId = "None",
                Evidence = _localizer["PrivacyLocalOnly"]
            });
        }

        return recommendations.Take(5).ToList();
    }

    public void Flush()
    {
        if (!_settings.LocalLearningEnabled)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        var recentSamples = _samples
            .OrderByDescending(sample => sample.Time)
            .Take(7200)
            .OrderBy(sample => sample.Time)
            .ToList();
        File.WriteAllText(_storePath, JsonSerializer.Serialize(recentSamples, JsonOptions));
        _lastFlush = DateTime.UtcNow;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return;
            }

            var samples = JsonSerializer.Deserialize<List<SessionLearningSample>>(File.ReadAllText(_storePath), JsonOptions);
            if (samples is not null)
            {
                _samples.AddRange(samples.TakeLast(7200));
            }
        }
        catch
        {
            _samples.Clear();
        }
    }

    private void AddSample(SystemSnapshot snapshot)
    {
        var topProcesses = snapshot.Processes
            .Where(process => !process.IsCritical)
            .OrderByDescending(process => process.CpuPercent + process.MemoryMb / 256)
            .Take(8)
            .Select(process => _settings.PrivacyMaximumMode ? HashName(process.Name) : process.Name)
            .ToList();

        _samples.Add(new SessionLearningSample
        {
            CpuPercent = snapshot.CpuUsagePercent,
            RamPercent = snapshot.RamUsagePercent,
            GpuPercent = snapshot.GpuUsagePercent,
            VramPercent = snapshot.VramUsagePercent,
            StarCitizenRunning = snapshot.Processes.Any(process => process.IsStarCitizen),
            TopProcesses = topProcesses
        });

        while (_samples.Count > 7200)
        {
            _samples.RemoveAt(0);
        }

        if ((DateTime.UtcNow - _lastFlush) > TimeSpan.FromMinutes(2))
        {
            Flush();
        }
    }

    private void AddPressureRecommendations(SystemSnapshot snapshot, List<Recommendation> recommendations)
    {
        if (snapshot.VramUsagePercent >= 85)
        {
            recommendations.Add(new Recommendation
            {
                Title = _localizer["AiHighVramTitle"],
                Severity = "High",
                Explanation = _localizer.Format("AiHighVramText", snapshot.VramUsagePercent),
                ActionText = _localizer["Review"],
                ActionId = "Review",
                Evidence = _localizer.Format("EvidenceVram", snapshot.VramUsedGb, snapshot.VramTotalGb)
            });
        }

        if (snapshot.RamUsagePercent >= _settings.RamThresholdPercent)
        {
            recommendations.Add(new Recommendation
            {
                Title = _localizer["AiHighRamTitle"],
                Severity = "Medium",
                Explanation = _localizer.Format("AiHighRamText", snapshot.RamUsagePercent),
                ActionText = _localizer["OptimizeRamNow"],
                ActionId = "OptimizeRam",
                Evidence = _localizer.Format("EvidenceRam", snapshot.RamUsedGb, snapshot.RamTotalGb)
            });
        }

        if (snapshot.CpuUsagePercent >= _settings.CpuThresholdPercent)
        {
            recommendations.Add(new Recommendation
            {
                Title = _localizer["AiHighCpuTitle"],
                Severity = "Medium",
                Explanation = _localizer.Format("AiHighCpuText", snapshot.CpuUsagePercent),
                ActionText = _localizer["OptimizeCpuNow"],
                ActionId = "OptimizeCpu",
                Evidence = _localizer.Format("EvidenceCpu", snapshot.CpuUsagePercent)
            });
        }
    }

    private void AddProcessRecommendations(SystemSnapshot snapshot, List<Recommendation> recommendations)
    {
        var process = snapshot.Processes
            .Where(process => !process.IsProtected && !process.IsCritical && process.IsBackground)
            .OrderByDescending(process => process.CpuPercent * 2 + process.MemoryMb / 256)
            .FirstOrDefault();

        if (process is null || process.CpuPercent < 8 && process.MemoryMb < 800)
        {
            return;
        }

        recommendations.Add(new Recommendation
        {
            Title = _localizer["AiBackgroundLoadTitle"],
            Severity = process.CpuPercent > 15 ? "High" : "Medium",
            Explanation = _localizer.Format("AiBackgroundLoadText", process.Name),
            ActionText = _localizer["Review"],
            ActionId = "Review",
            Evidence = $"{process.CpuPercent:0.0}% CPU, {process.MemoryMb:0} MB RAM"
        });
    }

    private void AddStarCitizenRecommendations(SystemSnapshot snapshot, List<Recommendation> recommendations)
    {
        if (!snapshot.Processes.Any(process => process.IsStarCitizen))
        {
            return;
        }

        var heavyCompanions = snapshot.Processes
            .Where(process => !process.IsProtected && IsCommonGamingCompanion(process.Name) && process.MemoryMb > 500)
            .OrderByDescending(process => process.MemoryMb)
            .Take(2)
            .ToList();

        foreach (var process in heavyCompanions)
        {
            recommendations.Add(new Recommendation
            {
                Title = _localizer["AiStarCitizenCompanionTitle"],
                Severity = "Medium",
                Explanation = _localizer.Format("AiStarCitizenCompanionText", process.Name),
                ActionText = _localizer["Review"],
                ActionId = "Review",
                Evidence = $"{process.MemoryMb:0} MB RAM"
            });
        }
    }

    private void AddCoreParkingRecommendations(SystemSnapshot snapshot, List<Recommendation> recommendations)
    {
        var parkedCores = snapshot.Cores.Count(core =>
            core.ParkingState.Equals("CoreStateParked", StringComparison.OrdinalIgnoreCase)
            || core.ParkingState.Equals(_localizer["CoreStateParked"], StringComparison.OrdinalIgnoreCase));

        if (parkedCores <= 0 || snapshot.CpuUsagePercent < 70)
        {
            return;
        }

        recommendations.Add(new Recommendation
        {
            Title = _localizer["AiCoreParkingTitle"],
            Severity = "Low",
            Explanation = _localizer.Format("AiCoreParkingText", parkedCores),
            ActionText = _localizer["OptimizePowerPlan"],
            ActionId = "PowerPlan",
            Evidence = _localizer["WindowsPowerOnly"]
        });
    }

    private static bool IsCommonGamingCompanion(string processName)
    {
        return processName.Contains("discord", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("chrome", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("msedge", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("obs", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("overwolf", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("steamwebhelper", StringComparison.OrdinalIgnoreCase);
    }

    private static string HashName(string processName)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(processName.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..12];
    }
}
