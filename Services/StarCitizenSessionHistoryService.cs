using System.IO;
using System.Text.Json;
using ANEVRED.Models;

namespace ANEVRED.Services;

public sealed class StarCitizenSessionHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;

    public StarCitizenSessionHistoryService(string appDataDirectory)
    {
        _path = Path.Combine(appDataDirectory, "starcitizen-sessions.json");
    }

    public IReadOnlyList<StarCitizenSessionView> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<StarCitizenSessionView>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void Save(IEnumerable<StarCitizenSessionView> sessions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? string.Empty);
        var json = JsonSerializer.Serialize(sessions.Take(250).ToList(), JsonOptions);
        File.WriteAllText(_path, json);
    }
}
