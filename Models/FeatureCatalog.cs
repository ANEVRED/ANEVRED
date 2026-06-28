namespace ANEVRED.Models;

public static class FeatureCatalog
{
    public static IReadOnlyList<FeatureDefinition> All { get; } =
    [
        new("Dashboard", "Dashboard", "⌂", null, true),
        new("AI", "AiRecommendations", "↯", nameof(AppSettings.FeatureAiEnabled)),
        new("Processes", "ProcessList", "▦", nameof(AppSettings.FeatureProcessesEnabled)),
        new("Gaming", "GamingMode", "G", nameof(AppSettings.FeatureGamingEnabled)),
        new("StarCitizen", "StarCitizenHub", "☆", nameof(AppSettings.FeatureStarCitizenEnabled)),
        new("Hardware", "HardwareMonitor", "▣", nameof(AppSettings.FeatureHardwareEnabled)),
        new("Macros", "MacroManager", "⌨", nameof(AppSettings.FeatureMacrosEnabled)),
        new("Logs", "Logs", "≡", nameof(AppSettings.FeatureLogsEnabled)),
        new("Settings", "Settings", "⚙", null, true),
        new("Info", "AppInfo", "i", null, true)
    ];

    public static IReadOnlySet<string> OptionalFeaturePropertyNames { get; } = All
        .Where(feature => feature.SettingsPropertyName is not null)
        .Select(feature => feature.SettingsPropertyName!)
        .ToHashSet(StringComparer.Ordinal);
}
