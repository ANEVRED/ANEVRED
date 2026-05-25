using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace ANEVRED.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    private Dictionary<string, string> _strings = [];

    public LocalizationService(string language)
    {
        SetLanguage(language);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CurrentLanguage { get; private set; } = "en";

    public string this[string key] => _strings.TryGetValue(key, out var value) ? value : key;

    public void SetLanguage(string language)
    {
        CurrentLanguage = string.IsNullOrWhiteSpace(language) ? "en" : language;
        _strings = LoadLanguage(CurrentLanguage);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
    }

    public string Format(string key, params object[] args)
    {
        return string.Format(this[key], args);
    }

    private static Dictionary<string, string> LoadLanguage(string language)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var languagePath = Path.Combine(baseDirectory, "Localization", language + ".json");
        if (!File.Exists(languagePath))
        {
            languagePath = Path.Combine(baseDirectory, "Localization", "en.json");
        }

        if (!File.Exists(languagePath))
        {
            return [];
        }

        var json = File.ReadAllText(languagePath);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
    }
}
