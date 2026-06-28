using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using ANEVRED.Models;

namespace ANEVRED.Services;

public sealed class MacroStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public MacroStorage(string appDataDirectory)
    {
        StoragePath = Path.Combine(appDataDirectory, "macros.json");
    }

    public string StoragePath { get; }

    public ObservableCollection<MacroDefinition> Load(IEnumerable<MacroDefinition>? legacyMacros = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        if (File.Exists(StoragePath))
        {
            try
            {
                var json = File.ReadAllText(StoragePath);
                return JsonSerializer.Deserialize<ObservableCollection<MacroDefinition>>(json, JsonOptions) ?? [];
            }
            catch
            {
                return [];
            }
        }

        var migrated = new ObservableCollection<MacroDefinition>(legacyMacros ?? []);
        if (migrated.Count > 0)
        {
            Save(migrated);
        }

        return migrated;
    }

    public void Save(IEnumerable<MacroDefinition> macros)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        var temporaryPath = StoragePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(macros, JsonOptions));
        File.Move(temporaryPath, StoragePath, true);
    }
}
