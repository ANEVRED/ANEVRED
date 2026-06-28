using System.Globalization;
using System.Windows.Data;

namespace ANEVRED.Services;

public sealed class MacroHotkeyDisplayConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var hotkey = values.ElementAtOrDefault(0)?.ToString() ?? string.Empty;
        var language = values.ElementAtOrDefault(1)?.ToString() ?? "en";
        if (!language.Equals("de", StringComparison.OrdinalIgnoreCase))
        {
            return hotkey;
        }

        return string.Join(" + ", hotkey
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => "Strg",
                "SHIFT" => "Umschalt",
                "MOUSELEFT" => "Linke Maustaste",
                "MOUSERIGHT" => "Rechte Maustaste",
                "MOUSEMIDDLE" => "Mittlere Maustaste",
                "MOUSEX1" => "Maustaste 4",
                "MOUSEX2" => "Maustaste 5",
                _ => part
            }));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
