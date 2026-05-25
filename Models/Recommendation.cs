namespace ANEVRED.Models;

public sealed class Recommendation
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Title { get; init; } = string.Empty;
    public string Severity { get; init; } = "Low";
    public string Explanation { get; init; } = string.Empty;
    public string ActionText { get; init; } = string.Empty;
    public string ActionId { get; init; } = "None";
    public string Evidence { get; init; } = string.Empty;
}
