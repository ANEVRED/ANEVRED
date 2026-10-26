using System.Globalization;
using System.IO;
using System.Text.Json;
using ANEVRED.Models;

namespace ANEVRED.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _saveLock = new();

    public SettingsService(string? appDataDirectory = null)
    {
        AppDataDirectory = appDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ANEVRED");
    }

    public string AppDataDirectory { get; }
    public string SettingsPath => Path.Combine(AppDataDirectory, "settings.json");
    public string BackupPath => SettingsPath + ".bak";
    public string? LoadWarning { get; private set; }

    public AppSettings Load()
    {
        LoadWarning = null;
        if (TryLoad(SettingsPath, out var settings)) return settings!;
        if (TryLoad(BackupPath, out settings))
        {
            LoadWarning = "Die Einstellungen konnten nicht gelesen werden. Die letzte gültige Sicherung wurde geladen.";
            return settings!;
        }

        var defaults = AppSettings.CreateDefault(DetectLanguage());
        // Seed only on first run; leave damaged files intact until a later save.
        if (!File.Exists(SettingsPath) && !File.Exists(BackupPath))
        {
            try
            {
                Save(defaults);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoadWarning = "Die Einstellungen konnten nicht angelegt oder gelesen werden. Vorläufig werden Standardwerte verwendet. " + ex.Message;
            }
        }
        else LoadWarning = "Einstellungen und Sicherung konnten nicht gelesen werden. Vorläufig werden Standardwerte verwendet; vorhandene Dateien bleiben erhalten.";
        return defaults;
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_saveLock)
        {
            settings.ProtectionRules ??= [];
            settings.ValidateAndClamp();
            Directory.CreateDirectory(AppDataDirectory);
            var temporaryPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, settings, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(SettingsPath))
                {
                    var validMain = TryLoad(SettingsPath, out _);
                    if (!validMain)
                    {
                        // Preserve the damaged bytes before replacing; retain the good backup.
                        var preservedPath = SettingsPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff")
                            + "-" + Guid.NewGuid().ToString("N") + ".json";
                        File.Copy(SettingsPath, preservedPath, overwrite: false);
                    }
                    File.Replace(temporaryPath, SettingsPath, validMain ? BackupPath : null);
                }
                else
                {
                    File.Move(temporaryPath, SettingsPath);
                    if (!File.Exists(BackupPath)) File.Copy(SettingsPath, BackupPath, overwrite: false);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }

    private static bool TryLoad(string path, out AppSettings? settings)
    {
        settings = null;
        try
        {
            using var stream = File.OpenRead(path);
            settings = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions);
            if (settings is null) return false;
            settings.ProtectionRules ??= [];
            settings.ValidateAndClamp();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException)
        {
            settings = null;
            return false;
        }
    }

    public static string DetectLanguage() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant() switch
    {
        "de" => "de",
        "ru" => "ru",
        _ => "en"
    };
}
