using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using ANEVRED.Models;

namespace ANEVRED.Services;

public sealed class MacroStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _saveLock = new();

    public MacroStorage(string appDataDirectory)
    {
        StoragePath = Path.Combine(appDataDirectory, "macros.json");
    }

    public string StoragePath { get; }
    public string BackupPath => StoragePath + ".bak";
    public string? LoadWarning { get; private set; }

    public ObservableCollection<MacroDefinition> Load(IEnumerable<MacroDefinition>? legacyMacros = null)
    {
        LoadWarning = null;
        if (TryLoad(StoragePath, out var macros)) return macros!;
        if (TryLoad(BackupPath, out macros))
        {
            LoadWarning = "Die Makrodatei konnte nicht gelesen werden. Die letzte gültige Sicherung wurde geladen.";
            return macros!;
        }
        if (File.Exists(StoragePath) || File.Exists(BackupPath))
        {
            LoadWarning = "Makrodatei und Sicherung konnten nicht gelesen werden. Die Makroliste ist vorläufig leer; vorhandene Dateien bleiben erhalten.";
            return [];
        }

        var migrated = new ObservableCollection<MacroDefinition>(legacyMacros ?? []);
        if (migrated.Count > 0)
        {
            try { Save(migrated); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoadWarning = "Die bisherigen Makros wurden geladen, konnten aber nicht gespeichert werden. " + ex.Message;
            }
        }
        return migrated;
    }

    public void Save(IEnumerable<MacroDefinition> macros)
    {
        ArgumentNullException.ThrowIfNull(macros);
        lock (_saveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
            var temporaryPath = StoragePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, macros, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                // Validate the actual bytes before replacing any previous data.
                if (!TryLoad(temporaryPath, out _)) throw new InvalidDataException("Die Makroliste enthält ungültige Einträge.");
                if (File.Exists(StoragePath))
                {
                    var validMain = TryLoad(StoragePath, out _);
                    if (!validMain)
                    {
                        var preservedPath = StoragePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff")
                            + "-" + Guid.NewGuid().ToString("N") + ".json";
                        File.Copy(StoragePath, preservedPath, overwrite: false);
                    }
                    File.Replace(temporaryPath, StoragePath, validMain ? BackupPath : null);
                }
                else
                {
                    File.Move(temporaryPath, StoragePath);
                    if (!File.Exists(BackupPath)) File.Copy(StoragePath, BackupPath, overwrite: false);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }

    private static bool TryLoad(string path, out ObservableCollection<MacroDefinition>? macros)
    {
        macros = null;
        try
        {
            using var stream = File.OpenRead(path);
            macros = JsonSerializer.Deserialize<ObservableCollection<MacroDefinition>>(stream, JsonOptions);
            if (macros is null || macros.Any(macro => macro is null || macro.Steps is null || macro.Steps.Any(step => step is null)))
            {
                macros = null;
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            macros = null;
            return false;
        }
    }
}
