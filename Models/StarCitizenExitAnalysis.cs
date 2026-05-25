namespace ANEVRED.Models;

public sealed class StarCitizenExitAnalysis
{
    public string StatusKey { get; init; } = "StarCitizenExitUnknown";
    public string Summary { get; init; } = string.Empty;
    public string LogPath { get; init; } = string.Empty;
    public IReadOnlyList<string> Evidence { get; init; } = [];
}
