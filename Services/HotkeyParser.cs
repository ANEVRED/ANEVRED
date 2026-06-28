using System.Windows.Input;

namespace ANEVRED.Services;

public readonly record struct ParsedHotkey(uint Modifiers, uint VirtualKey, string? MouseButton)
{
    public bool IsMouse => MouseButton is not null;
}

public static class HotkeyParser
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    private static readonly HashSet<string> MouseButtons = new(StringComparer.OrdinalIgnoreCase)
    {
        "MouseLeft", "MouseRight", "MouseMiddle", "MouseX1", "MouseX2"
    };

    public static bool TryParse(string? text, out ParsedHotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        uint modifiers = 0;
        uint key = 0;
        string? mouseButton = null;
        foreach (var rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Control", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Strg", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModControl;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModAlt;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Umschalt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModShift;
            }
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModWin;
            }
            else if (MouseButtons.Contains(part) && key == 0 && mouseButton is null)
            {
                mouseButton = NormalizeMouseButton(part);
            }
            else if (key == 0 && mouseButton is null)
            {
                key = ParseVirtualKey(part);
            }
            else
            {
                return false;
            }
        }

        if (key == 0 && mouseButton is null)
        {
            return false;
        }

        hotkey = new ParsedHotkey(modifiers, key, mouseButton);
        return true;
    }

    public static string Normalize(string? text)
    {
        if (!TryParse(text, out var parsed))
        {
            return text?.Trim() ?? string.Empty;
        }

        var parts = new List<string>();
        if ((parsed.Modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((parsed.Modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((parsed.Modifiers & ModShift) != 0) parts.Add("Shift");
        if ((parsed.Modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(parsed.IsMouse ? parsed.MouseButton! : FormatVirtualKey(parsed.VirtualKey));
        return string.Join("+", parts);
    }

    public static bool Conflicts(string? first, string? second)
    {
        return TryParse(first, out var left)
            && TryParse(second, out var right)
            && left == right;
    }

    private static uint ParseVirtualKey(string value)
    {
        if (value.Length == 1)
        {
            var character = char.ToUpperInvariant(value[0]);
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return character;
            }
        }

        if (value.Length is 2 or 3
            && value[0] is 'F' or 'f'
            && int.TryParse(value[1..], out var functionKey)
            && functionKey is >= 1 and <= 24)
        {
            return (uint)(0x70 + functionKey - 1);
        }

        var alias = value.ToUpperInvariant() switch
        {
            "ENTER" or "RETURN" => Key.Return,
            "SPACE" or "SPACEBAR" or "LEERTASTE" => Key.Space,
            "ESC" or "ESCAPE" => Key.Escape,
            "BACKSPACE" => Key.Back,
            "DEL" => Key.Delete,
            "INS" => Key.Insert,
            "PGUP" => Key.PageUp,
            "PGDN" => Key.PageDown,
            "UP" => Key.Up,
            "DOWN" => Key.Down,
            "LEFT" => Key.Left,
            "RIGHT" => Key.Right,
            _ => Enum.TryParse<Key>(value, true, out var parsedKey) ? parsedKey : Key.None
        };
        return alias == Key.None ? 0 : (uint)KeyInterop.VirtualKeyFromKey(alias);
    }

    private static string FormatVirtualKey(uint virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x70 + 1}";
        }

        var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        return key switch
        {
            Key.Return => "Enter",
            Key.Space => "Space",
            Key.Escape => "Escape",
            Key.Back => "Backspace",
            _ => key.ToString()
        };
    }

    private static string NormalizeMouseButton(string value)
    {
        return MouseButtons.First(button => button.Equals(value, StringComparison.OrdinalIgnoreCase));
    }
}
