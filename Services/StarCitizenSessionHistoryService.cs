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
    private readonly AppSettings _settings;

    public StarCitizenSessionHistoryService(string appDataDirectory, AppSettings settings)
    {
        _settings = settings;
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
            return (JsonSerializer.Deserialize<List<StarCitizenSessionView>>(json, JsonOptions) ?? [])
                .Where(IsRetained)
                .Take(DataRetentionPolicy.MaxStarCitizenSessions)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public void Save(IEnumerable<StarCitizenSessionView> sessions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? string.Empty);
        var retainedSessions = sessions
            .Where(IsRetained)
            .Take(DataRetentionPolicy.MaxStarCitizenSessions)
            .ToList();
        var json = JsonSerializer.Serialize(retainedSessions, JsonOptions);
        File.WriteAllText(_path, json);
    }

    private bool IsRetained(StarCitizenSessionView session)
    {
        var endedUtc = session.EndedUtc == default ? session.Ended.ToUniversalTime() : session.EndedUtc;
        return DataRetentionPolicy.IsRetainedUtc(endedUtc, _settings);
    }
}
