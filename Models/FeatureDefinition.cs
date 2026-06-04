namespace ANEVRED.Models;

public sealed record FeatureDefinition(
    string NavigationKey,
    string TitleKey,
    string Icon,
    string? SettingsPropertyName,
    bool IsRequired = false);
