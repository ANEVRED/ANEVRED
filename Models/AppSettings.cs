using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ZestResourceOptimizer.Models;

public sealed class AppSettings : INotifyPropertyChanged
{
    private double _ramThresholdPercent = 80;
    private double _cpuThresholdPercent = 85;
    private bool _autoModeEnabled;
    private bool _notificationsEnabled = true;
    private bool _startWithWindows;
    private string _language = "en";
    private string _theme = "Dark";
    private AppProfile _profile = AppProfile.Balanced;
    private int _processRefreshSeconds = 5;
    private int _maxDisplayedProcesses = 150;
    private int _autoRamIntervalMinutes = 15;
    private int _autoCpuIntervalMinutes = 5;
    private bool _localLearningEnabled = true;
    private bool _privacyMaximumMode = true;
    private bool _starCitizenHotkeysEnabled = true;
    private string _starCitizenServerChangeHotkey = "Ctrl+Alt+1";
    private string _starCitizenRespawnHotkey = "Ctrl+Alt+2";
    private string _starCitizenStutterHotkey = "Ctrl+Alt+3";
    private string _starCitizenPath = string.Empty;
    private bool _screenTranslationEnabled;
    private bool _screenTranslationAutoRefresh;
    private string _screenTranslationHotkey = "Ctrl+Alt+T";
    private string _screenTranslationCaptureHotkey = "Ctrl+Alt+R";
    private string _screenTranslationRegionHotkey = "Ctrl+Alt+Shift+R";
    private string _screenTranslationTargetLanguage = "de";
    private double _screenTranslationLeft = 520;
    private double _screenTranslationTop = 220;
    private double _screenTranslationWidth = 700;
    private double _screenTranslationHeight = 420;

    public event PropertyChangedEventHandler? PropertyChanged;

    public double RamThresholdPercent
    {
        get => _ramThresholdPercent;
        set => SetField(ref _ramThresholdPercent, Math.Clamp(value, 50, 98));
    }

    public double CpuThresholdPercent
    {
        get => _cpuThresholdPercent;
        set => SetField(ref _cpuThresholdPercent, Math.Clamp(value, 50, 100));
    }

    public bool AutoModeEnabled
    {
        get => _autoModeEnabled;
        set => SetField(ref _autoModeEnabled, value);
    }

    public bool NotificationsEnabled
    {
        get => _notificationsEnabled;
        set => SetField(ref _notificationsEnabled, value);
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetField(ref _startWithWindows, value);
    }

    public string Language
    {
        get => _language;
        set => SetField(ref _language, string.IsNullOrWhiteSpace(value) ? "en" : value);
    }

    public string Theme
    {
        get => _theme;
        set => SetField(ref _theme, string.IsNullOrWhiteSpace(value) ? "Dark" : value);
    }

    public AppProfile Profile
    {
        get => _profile;
        set => SetField(ref _profile, value);
    }

    public int ProcessRefreshSeconds
    {
        get => _processRefreshSeconds;
        set => SetField(ref _processRefreshSeconds, Math.Clamp(value, 3, 30));
    }

    public int MaxDisplayedProcesses
    {
        get => _maxDisplayedProcesses;
        set => SetField(ref _maxDisplayedProcesses, Math.Clamp(value, 50, 300));
    }

    public int AutoRamIntervalMinutes
    {
        get => _autoRamIntervalMinutes;
        set => SetField(ref _autoRamIntervalMinutes, Math.Clamp(value, 1, 60));
    }

    public int AutoCpuIntervalMinutes
    {
        get => _autoCpuIntervalMinutes;
        set => SetField(ref _autoCpuIntervalMinutes, Math.Clamp(value, 1, 60));
    }

    public bool LocalLearningEnabled
    {
        get => _localLearningEnabled;
        set => SetField(ref _localLearningEnabled, value);
    }

    public bool PrivacyMaximumMode
    {
        get => _privacyMaximumMode;
        set => SetField(ref _privacyMaximumMode, value);
    }

    public bool StarCitizenHotkeysEnabled
    {
        get => _starCitizenHotkeysEnabled;
        set => SetField(ref _starCitizenHotkeysEnabled, value);
    }

    public string StarCitizenServerChangeHotkey
    {
        get => _starCitizenServerChangeHotkey;
        set => SetField(ref _starCitizenServerChangeHotkey, NormalizeHotkey(value, "Ctrl+Alt+1"));
    }

    public string StarCitizenRespawnHotkey
    {
        get => _starCitizenRespawnHotkey;
        set => SetField(ref _starCitizenRespawnHotkey, NormalizeHotkey(value, "Ctrl+Alt+2"));
    }

    public string StarCitizenStutterHotkey
    {
        get => _starCitizenStutterHotkey;
        set => SetField(ref _starCitizenStutterHotkey, NormalizeHotkey(value, "Ctrl+Alt+3"));
    }

    public string StarCitizenPath
    {
        get => _starCitizenPath;
        set => SetField(ref _starCitizenPath, value?.Trim() ?? string.Empty);
    }

    public bool ScreenTranslationEnabled
    {
        get => _screenTranslationEnabled;
        set => SetField(ref _screenTranslationEnabled, value);
    }

    public bool ScreenTranslationAutoRefresh
    {
        get => _screenTranslationAutoRefresh;
        set => SetField(ref _screenTranslationAutoRefresh, value);
    }

    public string ScreenTranslationHotkey
    {
        get => _screenTranslationHotkey;
        set => SetField(ref _screenTranslationHotkey, NormalizeHotkey(value, "Ctrl+Alt+T"));
    }

    public string ScreenTranslationCaptureHotkey
    {
        get => _screenTranslationCaptureHotkey;
        set => SetField(ref _screenTranslationCaptureHotkey, NormalizeHotkey(value, "Ctrl+Alt+R"));
    }

    public string ScreenTranslationRegionHotkey
    {
        get => _screenTranslationRegionHotkey;
        set => SetField(ref _screenTranslationRegionHotkey, NormalizeHotkey(value, "Ctrl+Alt+Shift+R"));
    }

    public string ScreenTranslationTargetLanguage
    {
        get => _screenTranslationTargetLanguage;
        set => SetField(ref _screenTranslationTargetLanguage, string.IsNullOrWhiteSpace(value) ? "de" : value.Trim());
    }

    public double ScreenTranslationLeft
    {
        get => _screenTranslationLeft;
        set => SetField(ref _screenTranslationLeft, Math.Max(0, value));
    }

    public double ScreenTranslationTop
    {
        get => _screenTranslationTop;
        set => SetField(ref _screenTranslationTop, Math.Max(0, value));
    }

    public double ScreenTranslationWidth
    {
        get => _screenTranslationWidth;
        set => SetField(ref _screenTranslationWidth, Math.Clamp(value, 120, 4000));
    }

    public double ScreenTranslationHeight
    {
        get => _screenTranslationHeight;
        set => SetField(ref _screenTranslationHeight, Math.Clamp(value, 80, 3000));
    }

    public ObservableCollection<ProtectionRule> ProtectionRules { get; set; } = [];

    public static AppSettings CreateDefault(string language)
    {
        var settings = new AppSettings { Language = language };
        settings.ProtectionRules.Add(new ProtectionRule("StarCitizen.exe", true, "Star Citizen"));
        settings.ProtectionRules.Add(new ProtectionRule("StarCitizen*", true, "Star Citizen"));
        settings.ProtectionRules.Add(new ProtectionRule("RSI Launcher*", true, "RSI Launcher"));
        settings.ProtectionRules.Add(new ProtectionRule("RSI*", true, "RSI Launcher"));
        settings.ProtectionRules.Add(new ProtectionRule("TobiiEyeTracking.exe", true, "Tobii Eye Tracker"));
        settings.ProtectionRules.Add(new ProtectionRule("Tobii*", true, "Tobii Game Hub / Eye Tracker"));
        settings.ProtectionRules.Add(new ProtectionRule("EasyAntiCheat*", true, "Anti-Cheat"));
        settings.ProtectionRules.Add(new ProtectionRule("BattlEye*", true, "Anti-Cheat"));
        return settings;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private static string NormalizeHotkey(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

}
