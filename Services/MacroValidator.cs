using ANEVRED.Models;

namespace ANEVRED.Services;

public sealed record MacroValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static MacroValidationResult Success { get; } = new(true, []);
}

public sealed class MacroValidator
{
    private static readonly HashSet<string> MouseButtons = new(StringComparer.OrdinalIgnoreCase)
    {
        "Left", "Right", "Middle", "X1", "X2"
    };

    public MacroValidationResult Validate(
        MacroDefinition macro,
        IEnumerable<MacroDefinition>? allMacros = null,
        IEnumerable<string>? reservedHotkeys = null)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(macro.Name))
        {
            errors.Add("Der Name darf nicht leer sein.");
        }

        if (macro.Steps.Count == 0)
        {
            errors.Add("Das Makro benötigt mindestens einen Schritt.");
        }

        if (!string.IsNullOrWhiteSpace(macro.Hotkey) && !HotkeyParser.TryParse(macro.Hotkey, out _))
        {
            errors.Add($"Der Hotkey „{macro.Hotkey}“ ist ungültig.");
        }

        if (!string.IsNullOrWhiteSpace(macro.Hotkey))
        {
            var duplicate = allMacros?.FirstOrDefault(other =>
                other.Id != macro.Id && HotkeyParser.Conflicts(other.Hotkey, macro.Hotkey));
            if (duplicate is not null)
            {
                errors.Add($"Der Hotkey wird bereits von „{duplicate.Name}“ verwendet.");
            }

            var reserved = reservedHotkeys?.FirstOrDefault(value => HotkeyParser.Conflicts(value, macro.Hotkey));
            if (reserved is not null)
            {
                errors.Add($"Der Hotkey kollidiert mit der bestehenden Belegung „{reserved}“.");
            }
        }

        for (var index = 0; index < macro.Steps.Count; index++)
        {
            var step = macro.Steps[index];
            var prefix = $"Schritt {index + 1}: ";
            if (step.Type == MacroStepType.KeyPress)
            {
                if (!HotkeyParser.TryParse(step.Key, out var key))
                {
                    errors.Add(prefix + "Taste oder Tastenkombination ist ungültig.");
                }
                else if (key.IsMouse)
                {
                    errors.Add(prefix + "für Mausaktionen bitte den Typ Mausklick verwenden.");
                }
            }
            else if (step.Type == MacroStepType.MouseClick && !MouseButtons.Contains(step.MouseButton))
            {
                errors.Add(prefix + "ungültige Maustaste.");
            }
            else if (step.Type == MacroStepType.MouseWheel && step.WheelDelta == 0)
            {
                errors.Add(prefix + "Mausrad-Wert darf nicht 0 sein.");
            }
        }

        return errors.Count == 0 ? MacroValidationResult.Success : new(false, errors);
    }
}
