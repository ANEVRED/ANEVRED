using System.Globalization;
using System.IO;
using System.Text.Json;
using ANEVRED.Models;

namespace ANEVRED.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ANEVRED");

    private string LegacyAppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ANEVRED");

    public string SettingsPath => Path.Combine(AppDataDirectory, "settings.json");

    public AppSettings Load()
    {
        Directory.CreateDirectory(AppDataDirectory);
        MigrateLegacySettings();

        if (!File.Exists(SettingsPath))
        {
            var defaults = AppSettings.CreateDefault(DetectLanguage());
            Save(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null)
            {
                return AppSettings.CreateDefault(DetectLanguage());
            }

            settings.ValidateAndClamp();
            EnsureProtectionDefaults(settings);
            return settings;
        }
        catch
        {
            return AppSettings.CreateDefault(DetectLanguage());
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppDataDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }

    public static string DetectLanguage()
    {
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant() switch
        {
            "de" => "de",
            "ru" => "ru",
            _ => "en"
        };
    }

    private static void EnsureProtectionDefaults(AppSettings settings)
    {
        var defaults = AppSettings.CreateDefault(settings.Language);
        foreach (var rule in defaults.ProtectionRules)
        {
            if (settings.ProtectionRules.All(existing =>
                    !existing.Pattern.Equals(rule.Pattern, StringComparison.OrdinalIgnoreCase)))
            {
                settings.ProtectionRules.Add(rule);
            }
        }
    }

    private void MigrateLegacySettings()
    {
        var legacySettingsPath = Path.Combine(LegacyAppDataDirectory, "settings.json");
        if (File.Exists(SettingsPath) || !File.Exists(legacySettingsPath))
        {
            return;
        }

        File.Copy(legacySettingsPath, SettingsPath, overwrite: false);
    }
}
