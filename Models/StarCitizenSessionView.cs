namespace ANEVRED.Models;

public sealed class StarCitizenSessionView
{
    public DateTime StartedUtc { get; init; }
    public DateTime EndedUtc { get; init; }
    public DateTime Started { get; init; }
    public DateTime Ended { get; init; }
    public string StartedText { get; init; } = string.Empty;
    public string EndedText { get; init; } = string.Empty;
    public string DurationText { get; init; } = string.Empty;
    public string PeakSummary { get; init; } = string.Empty;
    public string ExitSummary { get; init; } = string.Empty;
    public string ExitDisplaySummary
    {
        get
        {
            var hintIndex = ExitSummary.IndexOf(" Hint:", StringComparison.OrdinalIgnoreCase);
            return hintIndex > 0 ? ExitSummary[..hintIndex].TrimEnd() : ExitSummary;
        }
    }

    public string LogEvidenceSummary { get; init; } = string.Empty;
    public List<string> LogEvidence { get; init; } = [];
}
