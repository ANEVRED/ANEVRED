namespace ANEVRED.Models;

public sealed class StarCitizenLogSession
{
    public DateTime StartedUtc { get; init; }
    public DateTime EndedUtc { get; init; }
    public string StatusKey { get; init; } = "StarCitizenExitUnknown";
    public string LogPath { get; init; } = string.Empty;
    public IReadOnlyList<string> Evidence { get; init; } = [];
}
