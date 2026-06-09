using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ANEVRED.Models;

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
    private bool _uiDimmingEnabled;
    private bool _uiDimmingAutoTuneEnabled;
    private string _uiDimmingHotkey = "Ctrl+Alt+D";
    private double _uiDimmingOpacityPercent = 30;
    private int _uiDimmingRed;
    private int _uiDimmingGreen;
    private int _uiDimmingBlue;
    private bool _uiColorFilterEnabled;
    private double _uiColorFilterRedPercent = 100;
    private double _uiColorFilterGreenPercent = 65;
    private double _uiColorFilterBluePercent = 100;
    private double _uiColorFilterContrastPercent = 100;
    private double _uiColorFilterBrightnessPercent;
    private double _uiColorFilterGammaPercent = 100;
    private double _uiColorFilterTemperature;
    private double _uiColorFilterTint;
    private string _mainWindowState = "Maximized";
    private double _mainWindowLeft = -1;
    private double _mainWindowTop = -1;
    private double _mainWindowWidth = 1280;
    private double _mainWindowHeight = 820;
    private bool _featureAiEnabled = true;
    private bool _featureProcessesEnabled = true;
    private bool _featureGamingEnabled = true;
    private bool _featureStarCitizenEnabled = true;
    private bool _featureHardwareEnabled = true;
    private bool _featureLogsEnabled = true;

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

    public bool UiDimmingEnabled
    {
        get => _uiDimmingEnabled;
        set => SetField(ref _uiDimmingEnabled, value);
    }

    public bool UiDimmingAutoTuneEnabled
    {
        get => _uiDimmingAutoTuneEnabled;
        set => SetField(ref _uiDimmingAutoTuneEnabled, value);
    }

    public string UiDimmingHotkey
    {
        get => _uiDimmingHotkey;
        set => SetField(ref _uiDimmingHotkey, NormalizeHotkey(value, "Ctrl+Alt+D"));
    }

    public double UiDimmingOpacityPercent
    {
        get => _uiDimmingOpacityPercent;
        set => SetField(ref _uiDimmingOpacityPercent, Math.Clamp(value, 0, 80));
    }

    public int UiDimmingRed
    {
        get => _uiDimmingRed;
        set => SetField(ref _uiDimmingRed, Math.Clamp(value, 0, 255));
    }

    public int UiDimmingGreen
    {
        get => _uiDimmingGreen;
        set => SetField(ref _uiDimmingGreen, Math.Clamp(value, 0, 255));
    }

    public int UiDimmingBlue
    {
        get => _uiDimmingBlue;
        set => SetField(ref _uiDimmingBlue, Math.Clamp(value, 0, 255));
    }

    public bool UiColorFilterEnabled
    {
        get => _uiColorFilterEnabled;
        set => SetField(ref _uiColorFilterEnabled, value);
    }

    public double UiColorFilterRedPercent
    {
        get => _uiColorFilterRedPercent;
        set => SetField(ref _uiColorFilterRedPercent, Math.Clamp(value, 50, 120));
    }

    public double UiColorFilterGreenPercent
    {
        get => _uiColorFilterGreenPercent;
        set => SetField(ref _uiColorFilterGreenPercent, Math.Clamp(value, 50, 120));
    }

    public double UiColorFilterBluePercent
    {
        get => _uiColorFilterBluePercent;
        set => SetField(ref _uiColorFilterBluePercent, Math.Clamp(value, 50, 120));
    }

    public double UiColorFilterContrastPercent
    {
        get => _uiColorFilterContrastPercent;
        set => SetField(ref _uiColorFilterContrastPercent, Math.Clamp(value, 50, 150));
    }

    public double UiColorFilterBrightnessPercent
    {
        get => _uiColorFilterBrightnessPercent;
        set => SetField(ref _uiColorFilterBrightnessPercent, Math.Clamp(value, -50, 50));
    }

    public double UiColorFilterGammaPercent
    {
        get => _uiColorFilterGammaPercent;
        set => SetField(ref _uiColorFilterGammaPercent, Math.Clamp(value, 50, 150));
    }

    public double UiColorFilterTemperature
    {
        get => _uiColorFilterTemperature;
        set => SetField(ref _uiColorFilterTemperature, Math.Clamp(value, -50, 50));
    }

    public double UiColorFilterTint
    {
        get => _uiColorFilterTint;
        set => SetField(ref _uiColorFilterTint, Math.Clamp(value, -50, 50));
    }

    public string MainWindowState
    {
        get => _mainWindowState;
        set => SetField(ref _mainWindowState, NormalizeWindowState(value));
    }

    public double MainWindowLeft
    {
        get => _mainWindowLeft;
        set => SetField(ref _mainWindowLeft, value);
    }

    public double MainWindowTop
    {
        get => _mainWindowTop;
        set => SetField(ref _mainWindowTop, value);
    }

    public double MainWindowWidth
    {
        get => _mainWindowWidth;
        set => SetField(ref _mainWindowWidth, Math.Clamp(value, 800, 5000));
    }

    public double MainWindowHeight
    {
        get => _mainWindowHeight;
        set => SetField(ref _mainWindowHeight, Math.Clamp(value, 500, 3000));
    }

    public bool FeatureAiEnabled
    {
        get => _featureAiEnabled;
        set => SetField(ref _featureAiEnabled, value);
    }

    public bool FeatureProcessesEnabled
    {
        get => _featureProcessesEnabled;
        set => SetField(ref _featureProcessesEnabled, value);
    }

    public bool FeatureGamingEnabled
    {
        get => _featureGamingEnabled;
        set => SetField(ref _featureGamingEnabled, value);
    }

    public bool FeatureStarCitizenEnabled
    {
        get => _featureStarCitizenEnabled;
        set => SetField(ref _featureStarCitizenEnabled, value);
    }

    public bool FeatureHardwareEnabled
    {
        get => _featureHardwareEnabled;
        set => SetField(ref _featureHardwareEnabled, value);
    }

    public bool FeatureLogsEnabled
    {
        get => _featureLogsEnabled;
        set => SetField(ref _featureLogsEnabled, value);
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
        settings.ProtectionRules.Add(new ProtectionRule("nvcontainer*", true, "NVIDIA driver/service"));
        settings.ProtectionRules.Add(new ProtectionRule("NVIDIA Share*", true, "NVIDIA overlay"));
        settings.ProtectionRules.Add(new ProtectionRule("RadeonSoftware*", true, "AMD driver/software"));
        settings.ProtectionRules.Add(new ProtectionRule("atieclxx*", true, "AMD driver/service"));
        settings.ProtectionRules.Add(new ProtectionRule("igfx*", true, "Intel graphics"));
        settings.ProtectionRules.Add(new ProtectionRule("audiodg*", true, "Windows audio"));
        settings.ProtectionRules.Add(new ProtectionRule("Nahimic*", true, "Audio service"));
        settings.ProtectionRules.Add(new ProtectionRule("lghub*", true, "Input device software"));
        settings.ProtectionRules.Add(new ProtectionRule("iCUE*", true, "Input device software"));
        settings.ProtectionRules.Add(new ProtectionRule("SteelSeries*", true, "Input device software"));
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

    private static string NormalizeWindowState(string? value)
    {
        return value?.Trim() switch
        {
            "Maximized" => "Maximized",
            "Minimized" => "Minimized",
            _ => "Normal"
        };
    }

    public bool GetFeatureEnabled(string settingsPropertyName)
    {
        return settingsPropertyName switch
        {
            nameof(FeatureAiEnabled) => FeatureAiEnabled,
            nameof(FeatureProcessesEnabled) => FeatureProcessesEnabled,
            nameof(FeatureGamingEnabled) => FeatureGamingEnabled,
            nameof(FeatureStarCitizenEnabled) => FeatureStarCitizenEnabled,
            nameof(FeatureHardwareEnabled) => FeatureHardwareEnabled,
            nameof(FeatureLogsEnabled) => FeatureLogsEnabled,
            _ => true
        };
    }

    public void SetFeatureEnabled(string settingsPropertyName, bool value)
    {
        switch (settingsPropertyName)
        {
            case nameof(FeatureAiEnabled):
                FeatureAiEnabled = value;
                break;
            case nameof(FeatureProcessesEnabled):
                FeatureProcessesEnabled = value;
                break;
            case nameof(FeatureGamingEnabled):
                FeatureGamingEnabled = value;
                break;
            case nameof(FeatureStarCitizenEnabled):
                FeatureStarCitizenEnabled = value;
                break;
            case nameof(FeatureHardwareEnabled):
                FeatureHardwareEnabled = value;
                break;
            case nameof(FeatureLogsEnabled):
                FeatureLogsEnabled = value;
                break;
        }
    }

    public void ValidateAndClamp()
    {
        RamThresholdPercent = RamThresholdPercent;
        CpuThresholdPercent = CpuThresholdPercent;
        Language = Language;
        Theme = Theme;
        ProcessRefreshSeconds = ProcessRefreshSeconds;
        MaxDisplayedProcesses = MaxDisplayedProcesses;
        AutoRamIntervalMinutes = AutoRamIntervalMinutes;
        AutoCpuIntervalMinutes = AutoCpuIntervalMinutes;
        StarCitizenServerChangeHotkey = StarCitizenServerChangeHotkey;
        StarCitizenRespawnHotkey = StarCitizenRespawnHotkey;
        StarCitizenStutterHotkey = StarCitizenStutterHotkey;
        UiDimmingHotkey = UiDimmingHotkey;
        UiDimmingOpacityPercent = UiDimmingOpacityPercent;
        UiDimmingRed = UiDimmingRed;
        UiDimmingGreen = UiDimmingGreen;
        UiDimmingBlue = UiDimmingBlue;
        UiColorFilterRedPercent = UiColorFilterRedPercent;
        UiColorFilterGreenPercent = UiColorFilterGreenPercent;
        UiColorFilterBluePercent = UiColorFilterBluePercent;
        UiColorFilterContrastPercent = UiColorFilterContrastPercent;
        UiColorFilterBrightnessPercent = UiColorFilterBrightnessPercent;
        UiColorFilterGammaPercent = UiColorFilterGammaPercent;
        UiColorFilterTemperature = UiColorFilterTemperature;
        UiColorFilterTint = UiColorFilterTint;
        MainWindowState = MainWindowState;
        MainWindowWidth = MainWindowWidth;
        MainWindowHeight = MainWindowHeight;
    }

}
