namespace ANEVRED.Models;

public sealed record MacroStepTypeOption(MacroStepType Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}
