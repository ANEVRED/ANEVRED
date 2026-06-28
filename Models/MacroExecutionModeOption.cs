namespace ANEVRED.Models;

public sealed record MacroExecutionModeOption(MacroExecutionMode Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}
