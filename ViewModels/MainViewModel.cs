using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using ANEVRED.Models;
using ANEVRED.Services;

namespace ANEVRED.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private static readonly string[] CurrentViewDependents =
    [
        nameof(IsDashboardView),
        nameof(IsAiView),
        nameof(IsProcessesView),
        nameof(IsGamingView),
        nameof(IsStarCitizenView),
        nameof(IsHardwareView),
        nameof(IsMacrosView),
        nameof(IsLogsView),
        nameof(IsSettingsView),
        nameof(IsInfoView)
    ];

    private static readonly string[] SelectedProcessDependents =
    [
        nameof(SelectedProcessInfoText)
    ];

    private static readonly string[] SelectedRecommendationDependents =
    [
        nameof(HasSelectedRecommendation),
        nameof(RecommendationDetailTitle),
        nameof(RecommendationDetailExplanation),
        nameof(RecommendationDetailEvidence),
        nameof(RecommendationDetailAction)
    ];

    private readonly SettingsService _settingsService = new();
    private readonly StartupService _startupService = new();
    private readonly ProcessProtectionService _protectionService;
    private readonly MonitoringService _monitoringService;
    private readonly OptimizationService _optimizationService;
    private readonly PowerPlanService _powerPlanService;
    private readonly MemoryCompressionService _memoryCompressionService;
    private readonly StarCitizenLogService _starCitizenLogService = new();
    private readonly StarCitizenSessionHistoryService _starCitizenSessionHistoryService;
    private readonly ShaderCacheService _shaderCacheService;
    private readonly LocalLearningService _learningService;
    private readonly DataStorageService _dataStorageService;
    private readonly MacroExecutionService _macroExecutionService;
    private readonly MacroStorage _macroStorage;
    private readonly MacroValidator _macroValidator = new();
    private readonly DispatcherTimer _timer = new();
    private SystemSnapshot? _lastSnapshot;
    private bool _isRefreshing;
    private DateTime _lastMemoryOptimization = DateTime.MinValue;
    private DateTime _lastCpuOptimization = DateTime.MinValue;
    private DateTime _nextAutoCpuOptimizationAllowed = DateTime.MinValue;
    private DateTime _lastNotification = DateTime.MinValue;
    private DateTime _lastProcessRefresh = DateTime.MinValue;
    private DateTime? _starCitizenSessionStarted;
    private DateTime? _starCitizenMissingSince;
    private SessionBaseline? _starCitizenSessionBaseline;
    private SessionBaseline? _starCitizenSessionPeak;
    private SessionBaseline? _lastMarkedEventBaseline;
    private TimeSpan _lastStarCitizenSessionDuration = TimeSpan.Zero;
    private long _starCitizenSessionLogOffset;
    private string _lastStarCitizenExitText = string.Empty;
    private IReadOnlyList<string> _lastStarCitizenExitEvidence = [];
    private int _selectedHistoryMinutes = 5;
    private string _newProtectionPattern = string.Empty;
    private string _currentView = "Dashboard";
    private ProcessSnapshot? _selectedProcess;
    private ProtectionRule? _selectedProtectionRule;
    private Recommendation? _selectedRecommendation;
    private string _hotkeyStatusText = string.Empty;
    private string _shaderCacheStatusText = string.Empty;
    private string _shaderCacheLastBackupText = string.Empty;
    private long _shaderCacheLastTotalBytes;
    private string? _shaderCacheLatestBackupPath;
    private bool _isShaderCacheBusy;
    private bool _isMacroHotkeyCaptureActive;
    private DateTime _lastAutoStutterDetected = DateTime.MinValue;
    private DateTime _lastAutoPressureDetected = DateTime.MinValue;
    private DateTime _lastAutoCpuPressureDetected = DateTime.MinValue;
    private readonly Queue<double> _starCitizenFrametimeWindow = new();
    private int _starCitizenConsecutiveStutterSamples;
    private int _starCitizenConsecutiveMemoryPressureSamples;
    private int _starCitizenConsecutiveCpuPressureSamples;
    private SessionBaseline? _lastAutoDetectionBaseline;
    private int _starCitizenAutoStutterCount;
    private int _starCitizenPressureSpikeCount;
    private int _starCitizenManualEventCount;
    private string _selectedStarCitizenPage = "Overview";
    private MacroDefinition? _selectedMacro;
    private MacroStep? _selectedMacroStep;
    private string _macroStatusText = string.Empty;

    public MainViewModel()
    {
        Settings = _settingsService.Load();
        L = new LocalizationService(Settings.Language);
        _shaderCacheStatusText = L["NotScanned"];
        _shaderCacheLastBackupText = L["NoBackup"];
        _macroStatusText = L["MacroReady"];
        var cleanedItems = new DataRetentionCleanupService(_settingsService.AppDataDirectory, Settings, message => AddLog("Info", message)).Cleanup();
        _protectionService = new ProcessProtectionService(Settings);
        _monitoringService = new MonitoringService(_protectionService);
        _optimizationService = new OptimizationService(Settings, _protectionService, L, AddLog);
        _powerPlanService = new PowerPlanService(L, AddLog);
        _memoryCompressionService = new MemoryCompressionService(L, AddLog);
        _dataStorageService = new DataStorageService(_settingsService.AppDataDirectory, Settings, message => AddLog("Info", message));
        _macroStorage = new MacroStorage(_settingsService.AppDataDirectory);
        Macros = _macroStorage.Load(Settings.Macros);
        _macroExecutionService = new MacroExecutionService(L, AddLog);
        _macroExecutionService.ExecutionChanged += MacroExecutionChanged;
        _learningService = new LocalLearningService(Settings, L, _settingsService.AppDataDirectory);
        _starCitizenSessionHistoryService = new StarCitizenSessionHistoryService(_settingsService.AppDataDirectory, Settings);
        _shaderCacheService = new ShaderCacheService(_settingsService.AppDataDirectory, Settings);
        LoadStarCitizenSessions();

        AddProtectionRuleCommand = new RelayCommand(_ => AddProtectionRule(), _ => !string.IsNullOrWhiteSpace(NewProtectionPattern));
        RemoveProtectionRuleCommand = new RelayCommand(_ => RemoveProtectionRule(), _ => SelectedProtectionRule is not null);
        ProtectSelectedProcessCommand = new RelayCommand(_ => ProtectSelectedProcess(), _ => SelectedProcess is not null);
        OptimizeSelectedProcessCommand = new RelayCommand(_ => OptimizeSelectedProcess(), _ => SelectedProcess is not null);
        OptimizeMemoryCommand = new RelayCommand(_ => RunMemoryOptimization());
        OptimizeCpuCommand = new RelayCommand(_ => RunCpuOptimization());
        OptimizeVramCommand = new RelayCommand(_ => RunVramOptimization());
        ResetPrioritiesCommand = new RelayCommand(_ => _optimizationService.ResetChangedPriorities());
        ExportLogsCommand = new RelayCommand(_ => ExportLogs());
        OptimizePowerPlanCommand = new RelayCommand(async _ => await _powerPlanService.OptimizeCurrentPlanAsync());
        ApplyRecommendationCommand = new RelayCommand(ApplyRecommendation);
        IgnoreRecommendationCommand = new RelayCommand(IgnoreRecommendation);
        ShowRecommendationDetailsCommand = new RelayCommand(ShowRecommendationDetails);
        NavigateCommand = new RelayCommand(Navigate);
        EnableGameModeCommand = new RelayCommand(_ => EnableGameMode());
        StopUnnecessaryTasksCommand = new RelayCommand(_ => StopUnnecessaryTasks());
        DisableMemoryCompressionCommand = new RelayCommand(_ => _memoryCompressionService.Disable());
        EnableMemoryCompressionCommand = new RelayCommand(_ => _memoryCompressionService.Enable());
        MarkServerChangeCommand = new RelayCommand(_ => MarkStarCitizenEvent("Serverwechsel"));
        MarkRespawnCommand = new RelayCommand(_ => MarkStarCitizenEvent("Respawn"));
        MarkStutterCommand = new RelayCommand(_ => MarkStarCitizenEvent("Lag/Stutter"));
        ScanShaderCacheCommand = new RelayCommand(_ => ScanShaderCache(), _ => !_isShaderCacheBusy);
        BackupShaderCacheCommand = new RelayCommand(_ => BackupShaderCache(), _ => !_isShaderCacheBusy);
        ClearShaderCacheCommand = new RelayCommand(_ => ClearShaderCache(), _ => !_isShaderCacheBusy);
        RestoreShaderCacheCommand = new RelayCommand(_ => RestoreShaderCache(), _ => !_isShaderCacheBusy);
        RefreshDataStorageCommand = new RelayCommand(_ => RefreshDataStorage());
        CleanupExpiredDataCommand = new RelayCommand(_ => CleanupExpiredData());
        DeleteWorkDataCommand = new RelayCommand(_ => DeleteWorkData(keepSettings: true));
        DeleteAllNonSettingsDataCommand = new RelayCommand(_ => DeleteWorkData(keepSettings: false));
        AddMacroCommand = new RelayCommand(_ => AddMacro());
        RemoveMacroCommand = new RelayCommand(_ => RemoveSelectedMacro(), _ => SelectedMacro is not null);
        DuplicateMacroCommand = new RelayCommand(_ => DuplicateSelectedMacro(), _ => SelectedMacro is not null);
        SaveMacroCommand = new RelayCommand(_ => SaveSelectedMacro(), _ => SelectedMacro is not null);
        ClearMacroHotkeyCommand = new RelayCommand(_ => ClearSelectedMacroHotkey(), _ => SelectedMacro?.HasHotkey == true);
        AddMacroKeyStepCommand = new RelayCommand(_ => AddMacroStep(MacroStepType.KeyPress), _ => SelectedMacro is not null);
        AddMacroDelayStepCommand = new RelayCommand(_ => AddMacroStep(MacroStepType.Delay), _ => SelectedMacro is not null);
        AddMacroMouseMoveStepCommand = new RelayCommand(_ => AddMacroStep(MacroStepType.MouseMove), _ => SelectedMacro is not null);
        AddMacroMouseClickStepCommand = new RelayCommand(_ => AddMacroStep(MacroStepType.MouseClick), _ => SelectedMacro is not null);
        AddMacroMouseWheelStepCommand = new RelayCommand(_ => AddMacroStep(MacroStepType.MouseWheel), _ => SelectedMacro is not null);
        RemoveMacroStepCommand = new RelayCommand(_ => RemoveSelectedMacroStep(), _ => SelectedMacroStep is not null);
        MoveMacroStepUpCommand = new RelayCommand(_ => MoveSelectedMacroStep(-1), _ => CanMoveSelectedMacroStep(-1));
        MoveMacroStepDownCommand = new RelayCommand(_ => MoveSelectedMacroStep(1), _ => CanMoveSelectedMacroStep(1));
        RunMacroCommand = new RelayCommand(async _ => await RunSelectedMacroAsync(), _ => SelectedMacro is { Enabled: true });
        StopMacroCommand = new RelayCommand(_ => StopMacro(), _ => _macroExecutionService.IsRunning);

        Settings.PropertyChanged += SettingsChanged;
        Settings.ProtectionRules.CollectionChanged += ProtectionRulesChanged;
        foreach (var rule in Settings.ProtectionRules)
        {
            rule.PropertyChanged += ProtectionRuleChanged;
        }
        Macros.CollectionChanged += MacrosCollectionChanged;
        foreach (var macro in Macros)
        {
            SubscribeMacro(macro);
        }
        SelectedMacro = Macros.FirstOrDefault();

        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        RefreshFeatureToggles();
        RefreshNavigationItems();
        RefreshDataStorage();
        ScanShaderCache();
        if (cleanedItems > 0)
        {
            AddLog("Info", L.Format("LogDataRetentionCleanup", cleanedItems));
        }

        AddLog("Info", L["LogAppStarted"]);
    }

    public event EventHandler? ThemeChanged;
    public event Action<string, string>? NotificationRequested;
    public event Func<string, string, bool>? ConfirmationRequested;
    public event EventHandler? MacroHotkeysChanged;

    public AppSettings Settings { get; }
    public LocalizationService L { get; }
    public ObservableCollection<double> RamHistory { get; } = [];
    public ObservableCollection<double> CpuHistory { get; } = [];
    public ObservableCollection<double> GpuHistory { get; } = [];
    public ObservableCollection<double> VramHistory { get; } = [];
    public ObservableCollection<double> FrametimeHistory { get; } = [];
    public ObservableCollection<MetricCard> MetricCards { get; } = [];
    public ObservableCollection<NavigationItem> NavigationItems { get; } = [];
    public ObservableCollection<CoreMetric> Cores { get; } = [];
    public ObservableCollection<ProcessSnapshot> Processes { get; } = [];
    public ObservableCollection<Recommendation> Recommendations { get; } = [];
    public ObservableCollection<ProtectedProcessView> ProtectedProcesses { get; } = [];
    public ObservableCollection<StarCitizenEventView> StarCitizenEvents { get; } = [];
    public ObservableCollection<StarCitizenSessionView> StarCitizenSessions { get; } = [];
    public ObservableCollection<LogEntry> Logs { get; } = [];
    public ObservableCollection<FeatureToggleViewModel> FeatureToggles { get; } = [];
    public ObservableCollection<ShaderCacheTargetView> ShaderCacheTargets { get; } = [];
    public ObservableCollection<DataStorageItem> DataStorageItems { get; } = [];
    public ObservableCollection<MacroDefinition> Macros { get; }

    public IReadOnlyList<LanguageOption> SupportedLanguages { get; } =
    [
        new("de", "Deutsch"),
        new("en", "English"),
        new("ru", "Russian")
    ];

    public IReadOnlyList<int> HistoryWindows { get; } = [5, 15, 60];
    public IReadOnlyList<AppProfile> Profiles { get; } = Enum.GetValues<AppProfile>();
    public IReadOnlyList<string> Themes { get; } = ["Dark", "Light"];
    public IReadOnlyList<MacroStepTypeOption> MacroStepTypes =>
    [
        new(MacroStepType.KeyPress, L["MacroStepKeyPress"]),
        new(MacroStepType.Delay, L["MacroStepDelayAction"]),
        new(MacroStepType.MouseClick, L["MacroStepMouseClick"]),
        new(MacroStepType.MouseMove, L["MacroStepMouseMove"]),
        new(MacroStepType.MouseWheel, L["MacroStepMouseWheel"])
    ];
    public IReadOnlyList<MacroExecutionModeOption> MacroExecutionModes =>
    [
        new(MacroExecutionMode.Once, L["MacroModeOnce"]),
        new(MacroExecutionMode.Repeat, L["MacroModeRepeat"]),
        new(MacroExecutionMode.Toggle, L["MacroModeToggle"]),
        new(MacroExecutionMode.Hold, L["MacroModeHold"])
    ];
    public IReadOnlyList<LanguageOption> MacroMouseButtons =>
    [
        new("Left", L["MouseButtonLeft"]),
        new("Right", L["MouseButtonRight"]),
        new("Middle", L["MouseButtonMiddle"]),
        new("X1", L["MouseButton4"]),
        new("X2", L["MouseButton5"])
    ];
    public IReadOnlyList<LanguageOption> MacroWheelDirections =>
    [
        new("Up", L["MouseWheelUp"]),
        new("Down", L["MouseWheelDown"])
    ];
    public IReadOnlyList<LanguageOption> DataRetentionOptions
    {
        get
        {
            var options = new List<LanguageOption>
            {
                new(nameof(DataRetentionMode.Session), L["DataRetentionSession"]),
                new(nameof(DataRetentionMode.OneDay), L["DataRetentionOneDay"])
            };

            if (!Settings.PrivacyMaximumMode)
            {
                options.Add(new(nameof(DataRetentionMode.SevenDays), L["DataRetentionSevenDays"]));
                options.Add(new(nameof(DataRetentionMode.FourteenDays), L["DataRetentionFourteenDays"]));
                options.Add(new(nameof(DataRetentionMode.ThirtyDays), L["DataRetentionThirtyDays"]));
            }

            return options;
        }
    }

    public string SelectedDataRetentionModeCode
    {
        get => Settings.DataRetentionMode.ToString();
        set
        {
            if (Enum.TryParse<DataRetentionMode>(value, out var mode))
            {
                Settings.DataRetentionMode = mode;
            }
        }
    }

    public string AppLogoSource => ThemeMode.Equals("Light", StringComparison.OrdinalIgnoreCase)
        ? "Assets/AppLogoLight.png"
        : "Assets/AppLogo.png";

    public RelayCommand AddProtectionRuleCommand { get; }
    public RelayCommand RemoveProtectionRuleCommand { get; }
    public RelayCommand ProtectSelectedProcessCommand { get; }
    public RelayCommand OptimizeSelectedProcessCommand { get; }
    public RelayCommand OptimizeMemoryCommand { get; }
    public RelayCommand OptimizeCpuCommand { get; }
    public RelayCommand OptimizeVramCommand { get; }
    public RelayCommand ResetPrioritiesCommand { get; }
    public RelayCommand ExportLogsCommand { get; }
    public RelayCommand OptimizePowerPlanCommand { get; }
    public RelayCommand ApplyRecommendationCommand { get; }
    public RelayCommand IgnoreRecommendationCommand { get; }
    public RelayCommand ShowRecommendationDetailsCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public RelayCommand EnableGameModeCommand { get; }
    public RelayCommand StopUnnecessaryTasksCommand { get; }
    public RelayCommand DisableMemoryCompressionCommand { get; }
    public RelayCommand EnableMemoryCompressionCommand { get; }
    public RelayCommand MarkServerChangeCommand { get; }
    public RelayCommand MarkRespawnCommand { get; }
    public RelayCommand MarkStutterCommand { get; }
    public RelayCommand ScanShaderCacheCommand { get; }
    public RelayCommand BackupShaderCacheCommand { get; }
    public RelayCommand ClearShaderCacheCommand { get; }
    public RelayCommand RestoreShaderCacheCommand { get; }
    public RelayCommand RefreshDataStorageCommand { get; }
    public RelayCommand CleanupExpiredDataCommand { get; }
    public RelayCommand DeleteWorkDataCommand { get; }
    public RelayCommand DeleteAllNonSettingsDataCommand { get; }
    public RelayCommand AddMacroCommand { get; }
    public RelayCommand RemoveMacroCommand { get; }
    public RelayCommand DuplicateMacroCommand { get; }
    public RelayCommand SaveMacroCommand { get; }
    public RelayCommand ClearMacroHotkeyCommand { get; }
    public RelayCommand AddMacroKeyStepCommand { get; }
    public RelayCommand AddMacroDelayStepCommand { get; }
    public RelayCommand AddMacroMouseMoveStepCommand { get; }
    public RelayCommand AddMacroMouseClickStepCommand { get; }
    public RelayCommand AddMacroMouseWheelStepCommand { get; }
    public RelayCommand RemoveMacroStepCommand { get; }
    public RelayCommand MoveMacroStepUpCommand { get; }
    public RelayCommand MoveMacroStepDownCommand { get; }
    public RelayCommand RunMacroCommand { get; }
    public RelayCommand StopMacroCommand { get; }

    public MacroDefinition? SelectedMacro
    {
        get => _selectedMacro;
        set
        {
            if (SetField(ref _selectedMacro, value))
            {
                SelectedMacroStep = value?.Steps.FirstOrDefault();
                OnPropertyChanged(nameof(HasSelectedMacro));
                RaiseMacroCommandStates();
            }
        }
    }

    public MacroStep? SelectedMacroStep
    {
        get => _selectedMacroStep;
        set
        {
            if (SetField(ref _selectedMacroStep, value))
            {
                OnPropertyChanged(nameof(HasSelectedMacroStep));
                RaiseMacroCommandStates();
            }
        }
    }

    public bool HasSelectedMacro => SelectedMacro is not null;
    public bool HasSelectedMacroStep => SelectedMacroStep is not null;
    public bool IsMacroRunning => _macroExecutionService.IsRunning;
    public bool IsMacroHotkeyCaptureActive
    {
        get => _isMacroHotkeyCaptureActive;
        private set => SetField(ref _isMacroHotkeyCaptureActive, value);
    }

    public string MacroStatusText
    {
        get => _macroStatusText;
        private set => SetField(ref _macroStatusText, value);
    }

    public double RamUsagePercent => _lastSnapshot?.RamUsagePercent ?? 0;
    public double CpuUsagePercent => _lastSnapshot?.CpuUsagePercent ?? 0;
    public double GpuUsagePercent => _lastSnapshot?.GpuUsagePercent ?? 0;
    public double VramUsagePercent => _lastSnapshot?.VramUsagePercent ?? 0;
    public double PagefileUsagePercent => _lastSnapshot?.PagefileUsagePercent ?? 0;
    public string RamUsageText => _lastSnapshot is null ? "0%" : $"{_lastSnapshot.RamUsagePercent:0}%";
    public string RamGbText => _lastSnapshot is null ? "0 / 0 GB" : $"{_lastSnapshot.RamUsedGb:0.0} / {_lastSnapshot.RamTotalGb:0.0} GB";
    public string PagefileGbText => _lastSnapshot is null || _lastSnapshot.PagefileTotalGb <= 0 ? "N/A" : $"{_lastSnapshot.PagefileUsedGb:0.0} / {_lastSnapshot.PagefileTotalGb:0.0} GB";
    public string CpuUsageText => $"{CpuUsagePercent:0}%";
    public string GpuUsageText => _lastSnapshot?.IsGpuDataAvailable == true && IsFinite(_lastSnapshot.GpuUsagePercent) ? $"{_lastSnapshot.GpuUsagePercent:0}%" : "N/A";
    public string VramUsageText => HasValidVram(_lastSnapshot) ? $"{_lastSnapshot!.VramUsagePercent:0}%" : "N/A";
    public string VramGbText => HasValidVram(_lastSnapshot) ? $"{_lastSnapshot!.VramUsedGb:0.0} / {_lastSnapshot.VramTotalGb:0.0} GB" : L["NoGpuData"];
    public string FrametimeText => _lastSnapshot is not null && IsFinite(_lastSnapshot.AverageFrametimeMs) && _lastSnapshot.AverageFrametimeMs > 0 ? $"{_lastSnapshot.AverageFrametimeMs:0.0} ms" : "N/A";
    public string GpuTemperatureText => _lastSnapshot is not null && IsFinite(_lastSnapshot.TemperatureCelsius) && _lastSnapshot.TemperatureCelsius > 0 ? $"{_lastSnapshot.TemperatureCelsius:0} C" : "N/A";
    public string CpuTemperatureText => _lastSnapshot is not null && IsFinite(_lastSnapshot.CpuTemperatureCelsius) && _lastSnapshot.CpuTemperatureCelsius > 0 ? $"{_lastSnapshot.CpuTemperatureCelsius:0} C" : "N/A";
    public string HardwareHealthText => BuildHardwareHealthText();
    public string HardwarePressureText => BuildHardwarePressureText();
    public string HardwareInsightText => BuildHardwareInsightText();
    public string HardwareDetailCpuText => BuildHardwareDetailCpuText();
    public string HardwareDetailGpuText => BuildHardwareDetailGpuText();
    public string HardwareDetailMemoryText => BuildHardwareDetailMemoryText();
    public string ShaderCacheStatusText
    {
        get => _shaderCacheStatusText;
        private set => SetField(ref _shaderCacheStatusText, value);
    }
    public string ShaderCacheLastBackupText
    {
        get => _shaderCacheLastBackupText;
        private set => SetField(ref _shaderCacheLastBackupText, value);
    }
    public string ShaderCacheSummaryText => ShaderCacheTargets.Count == 0
        ? L["ShaderCacheNotFoundHint"]
        : L.Format("ShaderCacheLocationsSummary", ShaderCacheTargets.Count, ShaderCacheStatusText);
    public string ShaderCacheClearHintText => L["ShaderCacheClearHint"];
    public string ProcessCountText => Processes.Count.ToString();
    public string LastLogText => Logs.FirstOrDefault()?.Message ?? string.Empty;
    public string TrayText => $"RAM {RamUsagePercent:0}% | CPU {CpuUsagePercent:0}%";
    public string StarCitizenStatusText => _lastSnapshot?.Processes.Any(process => process.IsStarCitizen) == true ? L["StarCitizenDetected"] : L["StarCitizenNotDetected"];
    public string SessionTimeText => _starCitizenSessionStarted is null ? "00:00:00" : (DateTime.UtcNow - _starCitizenSessionStarted.Value).ToString(@"hh\:mm\:ss");
    public string LastSessionTimeText => _lastStarCitizenSessionDuration == TimeSpan.Zero ? "-" : _lastStarCitizenSessionDuration.ToString(@"hh\:mm\:ss");
    public string StarCitizenLogPathText => string.IsNullOrWhiteSpace(Settings.StarCitizenPath)
        ? L["StarCitizenLogAuto"]
        : Settings.StarCitizenPath;
    public bool IsStarCitizenPathMissing => string.IsNullOrWhiteSpace(Settings.StarCitizenPath);
    public string StarCitizenPathWarningText => L["StarCitizenPathWarning"];
    public string LastStarCitizenExitText => string.IsNullOrWhiteSpace(_lastStarCitizenExitText)
        ? L["StarCitizenExitNone"]
        : _lastStarCitizenExitText;
    public string StarCitizenSessionSummaryText => BuildStarCitizenSessionSummary();
    public string StarCitizenEventDeltaText => BuildStarCitizenEventDelta();
    public string DefenderHintText => BuildDefenderHint();
    public string StarCitizenRiskText => L.Format("SessionRiskValue", CalculateStarCitizenRiskScore(), BuildStarCitizenRiskReason());
    public string LastSessionDisplayText => L.Format("LastSessionValue", LastSessionTimeText);
    public string StarCitizenSessionTotalsText => BuildStarCitizenSessionTotals();
    public string StarCitizenSessionHealthText => BuildStarCitizenSessionHealthText();
    public string StarCitizenRecentProblemText => BuildStarCitizenRecentProblemText();
    public string StarCitizenAutoDetectionText => BuildStarCitizenAutoDetectionText();
    public string CurrentProfileText => CurrentProfile.ToString();
    public string VramPressureText => _lastSnapshot is null || !_lastSnapshot.IsGpuDataAvailable ? L["Unknown"] : PressureText(_lastSnapshot.VramUsagePercent);
    public string ProtectedStatusText => _lastSnapshot?.Processes.Any(process => process.IsStarCitizen && process.IsProtected) == true ? L["Protected"] : L["NotProtected"];
    public string AutoOptimizationStatusText => Settings.AutoModeEnabled ? L["On"] : L["Off"];
    public string LocalLearningStatusText => Settings.LocalLearningEnabled ? L["On"] : L["Off"];
    public string PrivacyStatusText => Settings.PrivacyMaximumMode ? L["PrivacyMaximum"] : L["PrivacyLocalOnly"];
    public string DataStoragePathText => _settingsService.AppDataDirectory;
    public string DataRetentionSummaryText => Settings.PrivacyMaximumMode
        ? L.Format("DataRetentionSummaryMaxPrivacy", DataRetentionDisplayText)
        : L.Format("DataRetentionSummary", DataRetentionDisplayText);
    public string DataRetentionHintText => Settings.PrivacyMaximumMode
        ? L["DataRetentionMaxPrivacyHint"]
        : L["DataRetentionFullOptionsHint"];
    public string DataRetentionDisplayText => Settings.DataRetentionMode switch
    {
        DataRetentionMode.Session => L["DataRetentionSession"],
        DataRetentionMode.OneDay => L["DataRetentionOneDay"],
        DataRetentionMode.SevenDays => L["DataRetentionSevenDays"],
        DataRetentionMode.ThirtyDays => L["DataRetentionThirtyDays"],
        _ => L["DataRetentionFourteenDays"]
    };
    public string DataStorageSummaryText => L.Format(
        "DataStorageSummary",
        DataStorageItems.Count,
        FormatBytes(DataStorageItems.Sum(item => item.SizeBytes)));
    public string PerformanceScoreText => CalculatePerformanceScore().ToString("0");
    public string PerformanceStateText => RamUsagePercent < 75 && CpuUsagePercent < 75 ? L["Optimal"] : RamUsagePercent < 90 && CpuUsagePercent < 90 ? L["Warning"] : L["Critical"];
    public MediaBrush PerformanceStateBrush => PerformanceStateText == L["Critical"]
        ? BrushFromRgb(239, 68, 68)
        : PerformanceStateText == L["Warning"]
            ? BrushFromRgb(245, 158, 11)
            : BrushFromRgb(34, 197, 94);
    public string ProcessRefreshSecondsText => L.Format("SecondsValue", Settings.ProcessRefreshSeconds);
    public string MaxDisplayedProcessesText => Settings.MaxDisplayedProcesses.ToString();
    public string RamThresholdPercentText => $"{Settings.RamThresholdPercent:0}%";
    public string CpuThresholdPercentText => $"{Settings.CpuThresholdPercent:0}%";
    public string AutoRamIntervalText => L.Format("MinutesValue", Settings.AutoRamIntervalMinutes);
    public string AutoCpuIntervalText => L.Format("MinutesValue", Settings.AutoCpuIntervalMinutes);
    public string UiDimmingOpacityText => $"{Settings.UiDimmingOpacityPercent:0}%";
    public string UiDimmingRedText => Settings.UiDimmingRed.ToString();
    public string UiDimmingGreenText => Settings.UiDimmingGreen.ToString();
    public string UiDimmingBlueText => Settings.UiDimmingBlue.ToString();
    public string UiColorFilterRedPercentText => $"{Settings.UiColorFilterRedPercent:0}%";
    public string UiColorFilterGreenPercentText => $"{Settings.UiColorFilterGreenPercent:0}%";
    public string UiColorFilterBluePercentText => $"{Settings.UiColorFilterBluePercent:0}%";
    public string UiColorFilterContrastPercentText => $"{Settings.UiColorFilterContrastPercent:0}%";
    public string UiColorFilterBrightnessPercentText => $"{Settings.UiColorFilterBrightnessPercent:+0;-0;0}%";
    public string UiColorFilterGammaPercentText => $"{Settings.UiColorFilterGammaPercent:0}%";
    public string UiColorFilterTemperatureText => $"{Settings.UiColorFilterTemperature:+0;-0;0}";
    public string UiColorFilterTintText => $"{Settings.UiColorFilterTint:+0;-0;0}";
    public MediaBrush UiDimmingPreviewBrush => BrushFromRgb(
        EffectiveDimmingPreviewColor(Settings.UiDimmingRed),
        EffectiveDimmingPreviewColor(Settings.UiDimmingGreen),
        EffectiveDimmingPreviewColor(Settings.UiDimmingBlue));
    public string StarCitizenHotkeySummary => Settings.StarCitizenHotkeysEnabled
        ? L.Format(
            "StarCitizenHotkeySummary",
            Settings.StarCitizenServerChangeHotkey,
            Settings.StarCitizenRespawnHotkey,
            Settings.StarCitizenStutterHotkey)
        : L["StarCitizenHotkeysOff"];
    public string HotkeyStatusText
    {
        get => _hotkeyStatusText;
        private set => SetField(ref _hotkeyStatusText, value);
    }
    public bool HasSelectedRecommendation => SelectedRecommendation is not null;
    public string RecommendationDetailTitle => SelectedRecommendation?.Title ?? L["RecommendationDetails"];
    public string RecommendationDetailExplanation => SelectedRecommendation?.Explanation ?? L["RecommendationDetailsEmpty"];
    public string RecommendationDetailEvidence => SelectedRecommendation?.Evidence ?? string.Empty;
    public string RecommendationDetailAction => SelectedRecommendation is null
        ? string.Empty
        : L.Format("RecommendationActionDetail", SelectedRecommendation.ActionText, SelectedRecommendation.Severity);
    public string SelectedProcessInfoText => SelectedProcess is null
        ? L["NoProcessSelected"]
        : L.Format(
            "SelectedProcessInfo",
            SelectedProcess.Name,
            SelectedProcess.Id,
            SelectedProcess.CpuPercent,
            SelectedProcess.MemoryMb,
            SelectedProcess.CommitMb,
            SelectedProcess.Priority,
            SelectedProcess.Status);

    public string CurrentView
    {
        get => _currentView;
        set
        {
            if (SetField(ref _currentView, string.IsNullOrWhiteSpace(value) ? "Dashboard" : value))
            {
                RefreshNavigationItems();
                NotifyStateChanged(CurrentViewDependents);
            }
        }
    }

    public bool IsDashboardView => CurrentView == "Dashboard";
    public bool IsAiView => CurrentView == "AI";
    public bool IsProcessesView => CurrentView == "Processes";
    public bool IsGamingView => CurrentView == "Gaming";
    public bool IsStarCitizenView => CurrentView == "StarCitizen";
    public bool IsHardwareView => CurrentView == "Hardware";
    public bool IsMacrosView => CurrentView == "Macros";
    public bool IsLogsView => CurrentView == "Logs";
    public bool IsSettingsView => CurrentView == "Settings";
    public bool IsInfoView => CurrentView == "Info";

    public int SelectedHistoryMinutes
    {
        get => _selectedHistoryMinutes;
        set
        {
            if (SetField(ref _selectedHistoryMinutes, value))
            {
                OnPropertyChanged(nameof(SelectedHistoryPoints));
            }
        }
    }

    public int SelectedHistoryPoints => SelectedHistoryMinutes * 60;

    public string SelectedStarCitizenPage
    {
        get => _selectedStarCitizenPage;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "Overview" : value;
            if (SetField(ref _selectedStarCitizenPage, next))
            {
                OnPropertyChanged(nameof(IsStarCitizenOverviewPage));
                OnPropertyChanged(nameof(IsStarCitizenLogsPage));
                OnPropertyChanged(nameof(IsStarCitizenHotkeysPage));
                OnPropertyChanged(nameof(IsStarCitizenCachePage));
                OnPropertyChanged(nameof(IsStarCitizenDiagnosticsPage));
            }
        }
    }

    public bool IsStarCitizenOverviewPage
    {
        get => SelectedStarCitizenPage == "Overview";
        set { if (value) SelectedStarCitizenPage = "Overview"; }
    }

    public bool IsStarCitizenLogsPage
    {
        get => SelectedStarCitizenPage == "Logs";
        set { if (value) SelectedStarCitizenPage = "Logs"; }
    }

    public bool IsStarCitizenHotkeysPage
    {
        get => SelectedStarCitizenPage == "Hotkeys";
        set { if (value) SelectedStarCitizenPage = "Hotkeys"; }
    }

    public bool IsStarCitizenCachePage
    {
        get => SelectedStarCitizenPage == "Cache";
        set { if (value) SelectedStarCitizenPage = "Cache"; }
    }

    public bool IsStarCitizenDiagnosticsPage
    {
        get => SelectedStarCitizenPage == "Diagnostics";
        set { if (value) SelectedStarCitizenPage = "Diagnostics"; }
    }

    public string CurrentLanguage
    {
        get => Settings.Language;
        set
        {
            if (Settings.Language.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Settings.Language = value;
            L.SetLanguage(value);
            _lastProcessRefresh = DateTime.MinValue;
            RefreshFeatureToggles();
            RefreshNavigationItems();
            RefreshLocalizedComputedText();
            SaveSettings();
            AddLog("Info", L["LogLanguageChanged"]);
            OnPropertyChanged();
        }
    }

    public string ThemeMode
    {
        get => Settings.Theme;
        set
        {
            if (Settings.Theme.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Settings.Theme = value;
            SaveSettings();
            ThemeChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AppLogoSource));
        }
    }

    public AppProfile CurrentProfile
    {
        get => Settings.Profile;
        set
        {
            if (Settings.Profile == value)
            {
                return;
            }

            Settings.Profile = value;
            ApplyProfile(value);
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentProfileText));
        }
    }

    public string NewProtectionPattern
    {
        get => _newProtectionPattern;
        set
        {
            if (SetField(ref _newProtectionPattern, value))
            {
                AddProtectionRuleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ProcessSnapshot? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (SetField(ref _selectedProcess, value))
            {
                ProtectSelectedProcessCommand.RaiseCanExecuteChanged();
                OptimizeSelectedProcessCommand.RaiseCanExecuteChanged();
                NotifyStateChanged(SelectedProcessDependents);
            }
        }
    }

    public ProtectionRule? SelectedProtectionRule
    {
        get => _selectedProtectionRule;
        set
        {
            if (SetField(ref _selectedProtectionRule, value))
            {
                RemoveProtectionRuleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public Recommendation? SelectedRecommendation
    {
        get => _selectedRecommendation;
        set
        {
            if (SetField(ref _selectedRecommendation, value))
            {
                NotifyStateChanged(SelectedRecommendationDependents);
            }
        }
    }

    private void NotifyStateChanged(IEnumerable<string> propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    public async Task RefreshAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            var now = DateTime.UtcNow;
            var processRefreshDue = (now - _lastProcessRefresh) >= TimeSpan.FromSeconds(Settings.ProcessRefreshSeconds);
            var includeProcesses = processRefreshDue || _lastSnapshot is null;
            var snapshot = await Task.Run(() => _monitoringService.GetSnapshot(includeProcesses, Settings.MaxDisplayedProcesses));
            if (!snapshot.ProcessDataUpdated && _lastSnapshot is not null)
            {
                snapshot = MergeWithPreviousProcesses(snapshot, _lastSnapshot.Processes);
            }

            if (snapshot.ProcessDataUpdated)
            {
                _lastProcessRefresh = now;
            }

            _lastSnapshot = snapshot;
            UpdateSnapshot(snapshot);
            MaybeNotifyHighLoad(snapshot);
            MaybeOptimize(snapshot);
        }
        catch (Exception ex)
        {
            AddLog("Warn", L.Format("LogMonitoringFailed", ex.Message));
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        SaveValidMacros();
        _macroExecutionService.ExecutionChanged -= MacroExecutionChanged;
        _macroExecutionService.Dispose();
        Macros.CollectionChanged -= MacrosCollectionChanged;
        foreach (var macro in Macros)
        {
            UnsubscribeMacro(macro);
        }
        _optimizationService.ResetChangedPriorities();
        _monitoringService.Dispose();
        _learningService.Flush();
        SaveStarCitizenSessions();
        SaveSettings();
    }

    private void UpdateSnapshot(SystemSnapshot snapshot)
    {
        AddHistory(RamHistory, snapshot.RamUsagePercent);
        AddHistory(CpuHistory, snapshot.CpuUsagePercent);
        AddHistory(GpuHistory, snapshot.GpuUsagePercent);
        AddHistory(VramHistory, snapshot.VramUsagePercent);
        AddHistory(FrametimeHistory, snapshot.AverageFrametimeMs);
        UpdateStarCitizenSession(snapshot);
        UpdateMetricCards(snapshot);
        UpdateRecommendations(snapshot);
        Replace(Cores, snapshot.Cores.Select(LocalizeCore));
        if (snapshot.ProcessDataUpdated)
        {
            var selectedProcessId = SelectedProcess?.Id;
            Replace(Processes, snapshot.Processes.Select(LocalizeProcess));
            SelectedProcess = selectedProcessId is null
                ? null
                : Processes.FirstOrDefault(process => process.Id == selectedProcessId.Value);
            UpdateProtectedProcesses(snapshot);
        }

        OnPropertyChanged(nameof(RamUsagePercent));
        OnPropertyChanged(nameof(CpuUsagePercent));
        OnPropertyChanged(nameof(GpuUsagePercent));
        OnPropertyChanged(nameof(VramUsagePercent));
        OnPropertyChanged(nameof(PagefileUsagePercent));
        OnPropertyChanged(nameof(RamUsageText));
        OnPropertyChanged(nameof(RamGbText));
        OnPropertyChanged(nameof(PagefileGbText));
        OnPropertyChanged(nameof(CpuUsageText));
        OnPropertyChanged(nameof(GpuUsageText));
        OnPropertyChanged(nameof(VramUsageText));
        OnPropertyChanged(nameof(VramGbText));
        OnPropertyChanged(nameof(FrametimeText));
        OnPropertyChanged(nameof(GpuTemperatureText));
        OnPropertyChanged(nameof(CpuTemperatureText));
        OnPropertyChanged(nameof(HardwareHealthText));
        OnPropertyChanged(nameof(HardwarePressureText));
        OnPropertyChanged(nameof(HardwareInsightText));
        OnPropertyChanged(nameof(HardwareDetailCpuText));
        OnPropertyChanged(nameof(HardwareDetailGpuText));
        OnPropertyChanged(nameof(HardwareDetailMemoryText));
        OnPropertyChanged(nameof(ProcessCountText));
        OnPropertyChanged(nameof(TrayText));
        OnPropertyChanged(nameof(StarCitizenStatusText));
        OnPropertyChanged(nameof(SessionTimeText));
        OnPropertyChanged(nameof(LastSessionTimeText));
        OnPropertyChanged(nameof(StarCitizenSessionSummaryText));
        OnPropertyChanged(nameof(StarCitizenEventDeltaText));
        OnPropertyChanged(nameof(DefenderHintText));
        OnPropertyChanged(nameof(StarCitizenRiskText));
        OnPropertyChanged(nameof(LastSessionDisplayText));
        OnPropertyChanged(nameof(StarCitizenSessionTotalsText));
        OnPropertyChanged(nameof(StarCitizenSessionHealthText));
        OnPropertyChanged(nameof(StarCitizenRecentProblemText));
        OnPropertyChanged(nameof(StarCitizenAutoDetectionText));
        OnPropertyChanged(nameof(CurrentProfileText));
        OnPropertyChanged(nameof(VramPressureText));
        OnPropertyChanged(nameof(ProtectedStatusText));
        OnPropertyChanged(nameof(AutoOptimizationStatusText));
        OnPropertyChanged(nameof(LocalLearningStatusText));
        OnPropertyChanged(nameof(PrivacyStatusText));
        OnPropertyChanged(nameof(PerformanceScoreText));
        OnPropertyChanged(nameof(PerformanceStateText));
        OnPropertyChanged(nameof(PerformanceStateBrush));
        OnPropertyChanged(nameof(SelectedProcessInfoText));
    }

    private void MaybeOptimize(SystemSnapshot snapshot)
    {
        if (!Settings.AutoModeEnabled)
        {
            return;
        }

        if (snapshot.RamUsagePercent >= Settings.RamThresholdPercent
            && (DateTime.UtcNow - _lastMemoryOptimization) > TimeSpan.FromMinutes(Settings.AutoRamIntervalMinutes))
        {
            _optimizationService.OptimizeMemory(snapshot.Processes, userRequested: false);
            _lastMemoryOptimization = DateTime.UtcNow;
        }

        var now = DateTime.UtcNow;
        if (snapshot.CpuUsagePercent >= Settings.CpuThresholdPercent
            && now >= _nextAutoCpuOptimizationAllowed
            && (now - _lastCpuOptimization) > TimeSpan.FromSeconds(45))
        {
            var attempted = _optimizationService.OptimizeCpu(snapshot.Processes, userRequested: false);
            _lastCpuOptimization = now;
            _nextAutoCpuOptimizationAllowed = attempted
                ? now.AddMinutes(Settings.AutoCpuIntervalMinutes)
                : now.AddMinutes(Math.Max(5, Settings.AutoCpuIntervalMinutes));
        }
    }

    private void MaybeNotifyHighLoad(SystemSnapshot snapshot)
    {
        if (!Settings.NotificationsEnabled || (DateTime.UtcNow - _lastNotification) < TimeSpan.FromMinutes(2))
        {
            return;
        }

        if (snapshot.RamUsagePercent >= Settings.RamThresholdPercent)
        {
            NotificationRequested?.Invoke(L["NotificationTitle"], L.Format("NotificationHighRam", snapshot.RamUsagePercent));
            _lastNotification = DateTime.UtcNow;
            return;
        }

        if (snapshot.CpuUsagePercent >= Settings.CpuThresholdPercent)
        {
            NotificationRequested?.Invoke(L["NotificationTitle"], L.Format("NotificationHighCpu", snapshot.CpuUsagePercent));
            _lastNotification = DateTime.UtcNow;
        }
    }

    private async void ScanShaderCache()
    {
        if (!BeginShaderCacheOperation(L["Scanning"]))
        {
            return;
        }

        try
        {
            var starCitizenPath = Settings.StarCitizenPath;
            var result = await Task.Run(() => BuildShaderCacheScanResult(starCitizenPath));
            ApplyShaderCacheScanResult(result);
        }
        catch (Exception ex)
        {
            ShaderCacheStatusText = L.Format("ScanFailed", ex.Message);
            OnPropertyChanged(nameof(ShaderCacheSummaryText));
        }
        finally
        {
            EndShaderCacheOperation();
        }
    }

    private async void BackupShaderCache()
    {
        if (!BeginShaderCacheOperation(L["CreatingBackup"]))
        {
            return;
        }

        try
        {
            var starCitizenPath = Settings.StarCitizenPath;
            var result = await Task.Run(() =>
            {
                var backupPath = _shaderCacheService.Backup(starCitizenPath);
                return (BackupPath: backupPath, Scan: BuildShaderCacheScanResult(starCitizenPath));
            });

            AddLog("Info", L.Format("LogShaderCacheBackupCreated", result.BackupPath));
            ApplyShaderCacheScanResult(result.Scan);
        }
        catch (Exception ex)
        {
            AddLog("Warn", L.Format("LogShaderCacheBackupFailed", ex.Message));
            ShaderCacheStatusText = L.Format("BackupFailed", ex.Message);
            OnPropertyChanged(nameof(ShaderCacheSummaryText));
        }
        finally
        {
            EndShaderCacheOperation();
        }
    }

    private async void ClearShaderCache()
    {
        if (IsStarCitizenRunning())
        {
            AddLog("Warn", L["ShaderCacheClearBlockedRunning"]);
            ShaderCacheStatusText = L["ShaderCacheClearBlockedRunning"];
            OnPropertyChanged(nameof(ShaderCacheSummaryText));
            return;
        }

        if (ConfirmationRequested?.Invoke(L["ShaderCacheClearConfirmTitle"], L["ShaderCacheClearConfirmText"]) != true)
        {
            ShaderCacheStatusText = L["ShaderCacheClearCanceled"];
            OnPropertyChanged(nameof(ShaderCacheSummaryText));
            return;
        }

        if (!BeginShaderCacheOperation(L["ClearingCache"]))
        {
            return;
        }

        try
        {
            var starCitizenPath = Settings.StarCitizenPath;
            var result = await Task.Run(() =>
            {
                var cleared = _shaderCacheService.Clear(starCitizenPath);
                return (Cleared: cleared, Scan: BuildShaderCacheScanResult(starCitizenPath));
            });

            AddLog("Info", L.Format("LogShaderCacheCleared", result.Cleared));
            ApplyShaderCacheScanResult(result.Scan);
        }
        catch (Exception ex)
        {
            AddLog("Warn", L.Format("LogShaderCacheClearFailed", ex.Message));
            ShaderCacheStatusText = L.Format("ClearFailed", ex.Message);
            OnPropertyChanged(nameof(ShaderCacheSummaryText));
        }
        finally
        {
            EndShaderCacheOperation();
        }
    }

    private async void RestoreShaderCache()
    {
        if (!BeginShaderCacheOperation(L["RestoringCache"]))
        {
            return;
        }

        try
        {
            var starCitizenPath = Settings.StarCitizenPath;
            var result = await Task.Run(() =>
            {
                var backupPath = _shaderCacheService.RestoreLatest();
                return (BackupPath: backupPath, Scan: BuildShaderCacheScanResult(starCitizenPath));
            });

            AddLog("Info", L.Format("LogShaderCacheRestored", result.BackupPath));
            ApplyShaderCacheScanResult(result.Scan);
        }
        catch (Exception ex)
        {
            AddLog("Warn", L.Format("LogShaderCacheRestoreFailed", ex.Message));
            ShaderCacheStatusText = L.Format("RestoreFailed", ex.Message);
            OnPropertyChanged(nameof(ShaderCacheSummaryText));
        }
        finally
        {
            EndShaderCacheOperation();
        }
    }

    private bool BeginShaderCacheOperation(string statusText)
    {
        if (_isShaderCacheBusy)
        {
            return false;
        }

        _isShaderCacheBusy = true;
        ShaderCacheStatusText = statusText;
        OnPropertyChanged(nameof(ShaderCacheSummaryText));
        RaiseShaderCacheCommandStateChanged();
        return true;
    }

    private void EndShaderCacheOperation()
    {
        _isShaderCacheBusy = false;
        RaiseShaderCacheCommandStateChanged();
    }

    private void RaiseShaderCacheCommandStateChanged()
    {
        ScanShaderCacheCommand.RaiseCanExecuteChanged();
        BackupShaderCacheCommand.RaiseCanExecuteChanged();
        ClearShaderCacheCommand.RaiseCanExecuteChanged();
        RestoreShaderCacheCommand.RaiseCanExecuteChanged();
    }

    private bool IsStarCitizenRunning()
    {
        return _lastSnapshot?.Processes.Any(process => process.IsStarCitizen) == true;
    }

    private ShaderCacheScanResult BuildShaderCacheScanResult(string? starCitizenPath)
    {
        var snapshot = _shaderCacheService.Scan(starCitizenPath);
        var targets = snapshot.Targets.Select(target => new ShaderCacheTargetView
        {
            Name = target.Name,
            Path = target.Path,
            SizeText = FormatBytes(GetDirectorySizeSafe(target.Path))
        }).ToList();

        return new ShaderCacheScanResult(targets, snapshot.TotalBytes, snapshot.LatestBackupPath);
    }

    private void ApplyShaderCacheScanResult(ShaderCacheScanResult result)
    {
        Replace(ShaderCacheTargets, result.Targets);
        _shaderCacheLastTotalBytes = result.TotalBytes;
        _shaderCacheLatestBackupPath = result.LatestBackupPath;
        UpdateShaderCacheDisplayText();
        OnPropertyChanged(nameof(ShaderCacheSummaryText));
    }

    private void UpdateShaderCacheDisplayText()
    {
        ShaderCacheStatusText = ShaderCacheTargets.Count == 0
            ? L["NotFound"]
            : L.Format("ReadyWithSize", FormatBytes(_shaderCacheLastTotalBytes));
        ShaderCacheLastBackupText = _shaderCacheLatestBackupPath is null
            ? L["NoBackup"]
            : L.Format("LatestManualBackup", Path.GetFileName(_shaderCacheLatestBackupPath));
    }

    private void RunMemoryOptimization()
    {
        if (_lastSnapshot is not null)
        {
            _optimizationService.OptimizeMemory(_lastSnapshot.Processes, userRequested: true);
        }
    }

    private void RunCpuOptimization()
    {
        if (_lastSnapshot is null)
        {
            return;
        }

        try
        {
            _optimizationService.OptimizeCpu(_lastSnapshot.Processes, userRequested: true);
        }
        catch (Exception ex)
        {
            AddLog("Warn", L.Format("LogCpuOptimizationFailed", ex.Message));
        }
    }

    private void RunVramOptimization()
    {
        if (_lastSnapshot is not null)
        {
            _optimizationService.OptimizeVramExperimental(_lastSnapshot.Processes);
        }
    }

    private void ProtectSelectedProcess()
    {
        var process = SelectedProcess;
        if (process is null)
        {
            return;
        }

        var pattern = string.IsNullOrWhiteSpace(process.ExecutableName) ? process.Name : process.ExecutableName;
        if (Settings.ProtectionRules.Any(rule => rule.Pattern.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
        {
            AddLog("Info", L.Format("LogSelectedProcessAlreadyProtected", process.Name));
            return;
        }

        var rule = new ProtectionRule(pattern, true, L["SelectedProcessRule"]);
        Settings.ProtectionRules.Add(rule);
        SaveSettings();
        AddLog("Info", L.Format("LogSelectedProcessProtected", process.Name, pattern));
    }

    private void OptimizeSelectedProcess()
    {
        var process = SelectedProcess;
        if (process is null)
        {
            return;
        }

        if (process.IsProtected || process.IsCritical || _protectionService.IsProtectedProcessName(process.Name))
        {
            AddLog("Warn", L.Format("LogSelectedProcessBlocked", process.Name));
            return;
        }

        AddLog("Info", L.Format("LogSelectedProcessOptimizeRequested", process.Name, process.Id));
        _optimizationService.OptimizeMemory([process], userRequested: true);
        _optimizationService.OptimizeCpu([process], userRequested: true);
    }

    private void AddProtectionRule()
    {
        var rule = new ProtectionRule(NewProtectionPattern.Trim(), true, L["UserRule"]);
        Settings.ProtectionRules.Add(rule);
        NewProtectionPattern = string.Empty;
        SaveSettings();
    }

    private void RemoveProtectionRule()
    {
        if (SelectedProtectionRule is null)
        {
            return;
        }

        SelectedProtectionRule.PropertyChanged -= ProtectionRuleChanged;
        Settings.ProtectionRules.Remove(SelectedProtectionRule);
        SelectedProtectionRule = null;
        SaveSettings();
    }

    private string BuildHardwareHealthText()
    {
        if (_lastSnapshot is null)
        {
            return L["WaitingForSensorData"];
        }

        if (CpuUsagePercent >= 90 || RamUsagePercent >= 90 || VramUsagePercent >= 90)
        {
            return L["HighPressure"];
        }

        if (CpuUsagePercent >= 75 || RamUsagePercent >= 75 || VramUsagePercent >= 75)
        {
            return L["Watch"];
        }

        return L["Stable"];
    }

    private string BuildHardwarePressureText()
    {
        if (_lastSnapshot is null)
        {
            return L["HardwareDataInitializing"];
        }

        return L.Format("HardwarePressureSummary", CpuUsageText, RamUsageText, GpuUsageText, VramUsageText);
    }

    private string BuildHardwareInsightText()
    {
        if (_lastSnapshot is null)
        {
            return L["HardwareCollectingBaseline"];
        }

        if (RamUsagePercent >= Settings.RamThresholdPercent)
        {
            return L["HardwareHighRamInsight"];
        }

        if (CpuUsagePercent >= Settings.CpuThresholdPercent)
        {
            return L["HardwareHighCpuInsight"];
        }

        if (_lastSnapshot.IsGpuDataAvailable && VramUsagePercent >= 85)
        {
            return L["HardwareHighVramInsight"];
        }

        if (_lastSnapshot.AverageFrametimeMs > 22)
        {
            return L["HardwareFrametimeInsight"];
        }

        return L["HardwareStableInsight"];
    }

    private string BuildHardwareDetailCpuText() => L.Format(
        "HardwareCpuDetails",
        CpuUsageText,
        CpuTemperatureText,
        Cores.Count,
        Settings.CpuThresholdPercent);

    private string BuildHardwareDetailGpuText() => L.Format(
        "HardwareGpuDetails",
        GpuUsageText,
        VramGbText,
        GpuTemperatureText,
        FrametimeText);

    private string BuildHardwareDetailMemoryText() => L.Format(
        "HardwareMemoryDetails",
        RamGbText,
        PagefileGbText,
        Settings.RamThresholdPercent,
        Settings.ProcessRefreshSeconds);

    private static long GetDirectorySizeSafe(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file =>
                {
                    try
                    {
                        return new FileInfo(file).Length;
                    }
                    catch
                    {
                        return 0L;
                    }
                });
        }
        catch
        {
            return 0L;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private void ApplyProfile(AppProfile profile)
    {
        switch (profile)
        {
            case AppProfile.Gaming:
                Settings.RamThresholdPercent = 78;
                Settings.CpuThresholdPercent = 82;
                break;
            case AppProfile.Balanced:
                Settings.RamThresholdPercent = 80;
                Settings.CpuThresholdPercent = 85;
                break;
            case AppProfile.Silent:
                Settings.RamThresholdPercent = 88;
                Settings.CpuThresholdPercent = 92;
                break;
        }
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.StartWithWindows))
        {
            try
            {
                _startupService.SetEnabled(Settings.StartWithWindows);
            }
            catch (Exception ex)
            {
                AddLog("Warn", L.Format("LogAutostartFailed", ex.Message));
            }
        }

        if (e.PropertyName is nameof(AppSettings.AutoModeEnabled)
            or nameof(AppSettings.LocalLearningEnabled)
            or nameof(AppSettings.PrivacyMaximumMode)
            or nameof(AppSettings.DataRetentionMode))
        {
            OnPropertyChanged(nameof(AutoOptimizationStatusText));
            OnPropertyChanged(nameof(LocalLearningStatusText));
            OnPropertyChanged(nameof(PrivacyStatusText));
            OnPropertyChanged(nameof(DataRetentionDisplayText));
            OnPropertyChanged(nameof(DataRetentionSummaryText));
            OnPropertyChanged(nameof(DataRetentionHintText));
            OnPropertyChanged(nameof(SelectedDataRetentionModeCode));
            OnPropertyChanged(nameof(DataRetentionOptions));
            RefreshDataStorage();
        }

        if (e.PropertyName is nameof(AppSettings.ProcessRefreshSeconds)
            or nameof(AppSettings.MaxDisplayedProcesses)
            or nameof(AppSettings.RamThresholdPercent)
            or nameof(AppSettings.CpuThresholdPercent)
            or nameof(AppSettings.AutoRamIntervalMinutes)
            or nameof(AppSettings.AutoCpuIntervalMinutes))
        {
            OnPropertyChanged(nameof(ProcessRefreshSecondsText));
            OnPropertyChanged(nameof(MaxDisplayedProcessesText));
            OnPropertyChanged(nameof(RamThresholdPercentText));
            OnPropertyChanged(nameof(CpuThresholdPercentText));
            OnPropertyChanged(nameof(AutoRamIntervalText));
            OnPropertyChanged(nameof(AutoCpuIntervalText));
        }

        if (e.PropertyName is nameof(AppSettings.UiDimmingOpacityPercent)
            or nameof(AppSettings.UiDimmingRed)
            or nameof(AppSettings.UiDimmingGreen)
            or nameof(AppSettings.UiDimmingBlue)
            or nameof(AppSettings.UiColorFilterRedPercent)
            or nameof(AppSettings.UiColorFilterGreenPercent)
            or nameof(AppSettings.UiColorFilterBluePercent)
            or nameof(AppSettings.UiColorFilterContrastPercent)
            or nameof(AppSettings.UiColorFilterBrightnessPercent)
            or nameof(AppSettings.UiColorFilterGammaPercent)
            or nameof(AppSettings.UiColorFilterTemperature)
            or nameof(AppSettings.UiColorFilterTint))
        {
            OnPropertyChanged(nameof(UiDimmingOpacityText));
            OnPropertyChanged(nameof(UiDimmingRedText));
            OnPropertyChanged(nameof(UiDimmingGreenText));
            OnPropertyChanged(nameof(UiDimmingBlueText));
            OnPropertyChanged(nameof(UiColorFilterRedPercentText));
            OnPropertyChanged(nameof(UiColorFilterGreenPercentText));
            OnPropertyChanged(nameof(UiColorFilterBluePercentText));
            OnPropertyChanged(nameof(UiColorFilterContrastPercentText));
            OnPropertyChanged(nameof(UiColorFilterBrightnessPercentText));
            OnPropertyChanged(nameof(UiColorFilterGammaPercentText));
            OnPropertyChanged(nameof(UiColorFilterTemperatureText));
            OnPropertyChanged(nameof(UiColorFilterTintText));
            OnPropertyChanged(nameof(UiDimmingPreviewBrush));
        }

        if (e.PropertyName is nameof(AppSettings.StarCitizenHotkeysEnabled)
            or nameof(AppSettings.StarCitizenServerChangeHotkey)
            or nameof(AppSettings.StarCitizenRespawnHotkey)
            or nameof(AppSettings.StarCitizenStutterHotkey))
        {
            OnPropertyChanged(nameof(StarCitizenHotkeySummary));
        }

        if (e.PropertyName == nameof(AppSettings.StarCitizenPath))
        {
            OnPropertyChanged(nameof(StarCitizenLogPathText));
            OnPropertyChanged(nameof(IsStarCitizenPathMissing));
            OnPropertyChanged(nameof(StarCitizenPathWarningText));
            ScanShaderCache();
        }

        if (e.PropertyName is not null && FeatureCatalog.OptionalFeaturePropertyNames.Contains(e.PropertyName))
        {
            RefreshNavigationItems();
            EnsureCurrentViewAvailable();
        }

        SaveSettings();
    }

    private void ProtectionRulesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (ProtectionRule rule in e.NewItems)
            {
                rule.PropertyChanged += ProtectionRuleChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (ProtectionRule rule in e.OldItems)
            {
                rule.PropertyChanged -= ProtectionRuleChanged;
            }
        }

        SaveSettings();
    }

    private void ProtectionRuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        SaveSettings();
    }

    private void ApplyRecommendation(object? parameter)
    {
        if (parameter is not Recommendation recommendation)
        {
            return;
        }

        SelectedRecommendation = recommendation;
        switch (recommendation.ActionId)
        {
            case "OptimizeRam":
                RunMemoryOptimization();
                break;
            case "OptimizeCpu":
                RunCpuOptimization();
                break;
            case "PowerPlan":
                _ = _powerPlanService.OptimizeCurrentPlanAsync();
                break;
            default:
                AddLog("Info", recommendation.Title + ": " + recommendation.Evidence);
                break;
        }
    }

    private void IgnoreRecommendation(object? parameter)
    {
        if (parameter is Recommendation recommendation)
        {
            Recommendations.Remove(recommendation);
            if (ReferenceEquals(SelectedRecommendation, recommendation))
            {
                SelectedRecommendation = null;
            }
        }
    }

    private void ShowRecommendationDetails(object? parameter)
    {
        if (parameter is Recommendation recommendation)
        {
            SelectedRecommendation = recommendation;
            AddLog("Info", L.Format("LogRecommendationDetails", recommendation.Title, recommendation.Evidence));
        }
    }

    private void Navigate(object? parameter)
    {
        if (parameter is string view && IsFeatureVisible(view))
        {
            CurrentView = view;
        }
    }

    private void EnableGameMode()
    {
        CurrentProfile = AppProfile.Gaming;
        Settings.AutoModeEnabled = true;
        Settings.ProcessRefreshSeconds = Math.Max(Settings.ProcessRefreshSeconds, 5);
        SaveSettings();
        AddLog("Info", L["LogGameModeEnabled"]);
    }

    private void StopUnnecessaryTasks()
    {
        if (_lastSnapshot is not null)
        {
            var candidates = _optimizationService.GetGamingKillSwitchCandidates(_lastSnapshot.Processes);
            if (candidates.Count > 0)
            {
                var processList = string.Join(
                    Environment.NewLine,
                    candidates
                        .Take(12)
                        .Select(process => $"{process.Name} (PID {process.Id}, RAM {process.MemoryMb:0} MB, Commit {process.CommitMb:0} MB)"));

                if (candidates.Count > 12)
                {
                    processList += Environment.NewLine + $"... +{candidates.Count - 12}";
                }

                if (ConfirmationRequested?.Invoke(
                        L["StopTasksConfirmTitle"],
                        L.Format("StopTasksConfirmText", processList)) != true)
                {
                    AddLog("Info", L["StopTasksCanceled"]);
                    return;
                }
            }

            _optimizationService.RunGamingKillSwitch(_lastSnapshot.Processes);
        }

        RunCpuOptimization();
        RunMemoryOptimization();
        AddLog("Info", L["LogStopTasksSafe"]);
    }

    private void ExportLogs()
    {
        var path = Path.Combine(_settingsService.AppDataDirectory, $"logs-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var lines = Logs.Reverse().SelectMany(log =>
        {
            var header = $"{log.Time:O}\t{log.Level}\t{log.Message}";
            return log.Details.Count == 0
                ? Enumerable.Repeat(header, 1)
                : new[] { header }.Concat(log.Details.Select(detail => $"{log.Time:O}\t{log.Level}\t  {detail}"));
        });
        File.WriteAllLines(path, lines);
        AddLog("Info", L.Format("LogExported", path));
        RefreshDataStorage();
    }

    private void RefreshLocalizedComputedText()
    {
        OnPropertyChanged(nameof(DataRetentionOptions));
        OnPropertyChanged(nameof(SelectedDataRetentionModeCode));
        OnPropertyChanged(nameof(DataRetentionDisplayText));
        OnPropertyChanged(nameof(DataRetentionSummaryText));
        OnPropertyChanged(nameof(DataRetentionHintText));
        OnPropertyChanged(nameof(DataStorageSummaryText));
        OnPropertyChanged(nameof(MacroStepTypes));
        OnPropertyChanged(nameof(MacroExecutionModes));
        OnPropertyChanged(nameof(MacroMouseButtons));
        OnPropertyChanged(nameof(MacroWheelDirections));
        if (!_macroExecutionService.IsRunning)
        {
            MacroStatusText = L["MacroReady"];
        }
        OnPropertyChanged(nameof(AutoOptimizationStatusText));
        OnPropertyChanged(nameof(LocalLearningStatusText));
        OnPropertyChanged(nameof(PrivacyStatusText));
        OnPropertyChanged(nameof(ProcessRefreshSecondsText));
        OnPropertyChanged(nameof(AutoRamIntervalText));
        OnPropertyChanged(nameof(AutoCpuIntervalText));
        OnPropertyChanged(nameof(StarCitizenLogPathText));
        OnPropertyChanged(nameof(StarCitizenPathWarningText));
        OnPropertyChanged(nameof(StarCitizenSessionHealthText));
        OnPropertyChanged(nameof(StarCitizenRecentProblemText));
        OnPropertyChanged(nameof(StarCitizenAutoDetectionText));
        OnPropertyChanged(nameof(HardwareHealthText));
        OnPropertyChanged(nameof(HardwarePressureText));
        OnPropertyChanged(nameof(HardwareInsightText));
        OnPropertyChanged(nameof(HardwareDetailCpuText));
        OnPropertyChanged(nameof(HardwareDetailGpuText));
        OnPropertyChanged(nameof(HardwareDetailMemoryText));
        OnPropertyChanged(nameof(ShaderCacheSummaryText));
        OnPropertyChanged(nameof(ShaderCacheClearHintText));
        if (!_isShaderCacheBusy)
        {
            UpdateShaderCacheDisplayText();
        }
    }

    private void AddMacro()
    {
        var macro = new MacroDefinition
        {
            Name = L.Format("NewMacroName", Macros.Count + 1),
            Hotkey = string.Empty
        };
        Macros.Add(macro);
        SelectedMacro = macro;
    }

    private void RemoveSelectedMacro()
    {
        var macro = SelectedMacro;
        if (macro is null
            || ConfirmationRequested?.Invoke(
                L["DeleteMacroConfirmTitle"],
                L.Format("DeleteMacroConfirmText", macro.Name)) != true)
        {
            return;
        }

        _macroExecutionService.Stop(macro.Id);
        var index = Macros.IndexOf(macro);
        Macros.Remove(macro);
        SelectedMacro = Macros.Count == 0
            ? null
            : Macros[Math.Clamp(index, 0, Macros.Count - 1)];
        SaveValidMacros();
    }

    private void DuplicateSelectedMacro()
    {
        if (SelectedMacro is null)
        {
            return;
        }

        var clone = SelectedMacro.Clone();
        clone.Name = L.Format("MacroCopyName", SelectedMacro.Name);
        Macros.Add(clone);
        SelectedMacro = clone;
        MacroStatusText = L["MacroDuplicated"];
    }

    private void SaveSelectedMacro()
    {
        if (SelectedMacro is null)
        {
            return;
        }

        var validation = ValidateMacro(SelectedMacro);
        if (!validation.IsValid)
        {
            SelectedMacro.RunState = MacroRunState.Error;
            SelectedMacro.LastError = string.Join(Environment.NewLine, validation.Errors);
            MacroStatusText = validation.Errors[0];
            return;
        }

        SelectedMacro.Hotkey = HotkeyParser.Normalize(SelectedMacro.Hotkey);
        SelectedMacro.LastError = string.Empty;
        if (SelectedMacro.RunState == MacroRunState.Error)
        {
            SelectedMacro.RunState = MacroRunState.Stopped;
        }

        _macroStorage.Save(Macros);
        MacroStatusText = L["MacroSaved"];
        MacroHotkeysChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearSelectedMacroHotkey()
    {
        if (SelectedMacro is null)
        {
            return;
        }

        SelectedMacro.Hotkey = string.Empty;
        MacroStatusText = L["MacroHotkeyRemoved"];
        SaveValidMacros();
    }

    private void AddMacroStep(MacroStepType type)
    {
        if (SelectedMacro is null || SelectedMacro.Steps.Count >= 100)
        {
            return;
        }

        var step = type switch
        {
            MacroStepType.Delay => new MacroStep { Type = type, DelayMs = 100 },
            MacroStepType.MouseClick => new MacroStep { Type = type, MouseButton = "Left" },
            MacroStepType.MouseWheel => new MacroStep { Type = type, WheelDelta = 120 },
            _ => new MacroStep { Type = type }
        };
        SelectedMacro.Steps.Add(step);
        SelectedMacroStep = step;
        MacroStatusText = L.Format("MacroStepAdded", GetMacroStepTypeName(type));
    }

    private void RemoveSelectedMacroStep()
    {
        if (SelectedMacro is null || SelectedMacroStep is null)
        {
            return;
        }

        var index = SelectedMacro.Steps.IndexOf(SelectedMacroStep);
        SelectedMacro.Steps.Remove(SelectedMacroStep);
        SelectedMacroStep = SelectedMacro.Steps.Count == 0
            ? null
            : SelectedMacro.Steps[Math.Clamp(index, 0, SelectedMacro.Steps.Count - 1)];
        MacroStatusText = L["MacroStepRemoved"];
    }

    private bool CanMoveSelectedMacroStep(int offset)
    {
        if (SelectedMacro is null || SelectedMacroStep is null)
        {
            return false;
        }

        var index = SelectedMacro.Steps.IndexOf(SelectedMacroStep);
        var target = index + offset;
        return index >= 0 && target >= 0 && target < SelectedMacro.Steps.Count;
    }

    private void MoveSelectedMacroStep(int offset)
    {
        if (!CanMoveSelectedMacroStep(offset) || SelectedMacro is null || SelectedMacroStep is null)
        {
            return;
        }

        var index = SelectedMacro.Steps.IndexOf(SelectedMacroStep);
        SelectedMacro.Steps.Move(index, index + offset);
        MacroStatusText = offset < 0 ? L["MacroStepMovedUp"] : L["MacroStepMovedDown"];
        RaiseMacroCommandStates();
    }

    private async Task RunSelectedMacroAsync()
    {
        if (SelectedMacro is null)
        {
            return;
        }

        await RunMacroAsync(SelectedMacro.Id);
    }

    public async Task RunMacroAsync(Guid macroId)
    {
        var macro = Macros.FirstOrDefault(item => item.Id == macroId);
        if (macro is null || !macro.Enabled)
        {
            return;
        }

        var validation = ValidateMacro(macro);
        if (!validation.IsValid)
        {
            macro.RunState = MacroRunState.Error;
            macro.LastError = string.Join(Environment.NewLine, validation.Errors);
            MacroStatusText = validation.Errors[0];
            return;
        }

        if (macro.ExecutionMode == MacroExecutionMode.Toggle && _macroExecutionService.IsRunningMacro(macro.Id))
        {
            _macroExecutionService.Stop(macro.Id);
            return;
        }

        MacroStatusText = L.Format("MacroRunning", macro.Name);
        await _macroExecutionService.StartAsync(macro);
    }

    private void StopMacro()
    {
        if (SelectedMacro is not null)
        {
            _macroExecutionService.Stop(SelectedMacro.Id);
        }
        else
        {
            _macroExecutionService.StopAll();
        }
        MacroStatusText = L["MacroStopping"];
        RaiseMacroCommandStates();
    }

    public void ReleaseMacroHotkey(Guid macroId)
    {
        var macro = Macros.FirstOrDefault(item => item.Id == macroId);
        if (macro?.ExecutionMode == MacroExecutionMode.Hold)
        {
            _macroExecutionService.Stop(macroId);
        }
    }

    public void SetMacroHotkey(Guid macroId, string hotkey)
    {
        var macro = Macros.FirstOrDefault(item => item.Id == macroId);
        if (macro is null)
        {
            return;
        }

        macro.Hotkey = HotkeyParser.Normalize(hotkey);
        SelectedMacro = macro;
    }

    public void SetSelectedMacroStepKey(string key)
    {
        if (SelectedMacroStep is not { Type: MacroStepType.KeyPress })
        {
            return;
        }

        SelectedMacroStep.Key = key;
        MacroStatusText = L.Format("MacroStepKeySet", key);
    }

    public void SetMacroCaptureState(bool active, string message)
    {
        IsMacroHotkeyCaptureActive = active;
        MacroStatusText = message;
    }

    public void SetMacroHotkeyRegistrationResult(string hotkey, bool registered)
    {
        MacroStatusText = registered
            ? L.Format("MacroHotkeySet", hotkey)
            : L.Format("MacroHotkeyRegistrationFailed", hotkey);
    }

    private void MacrosCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (MacroDefinition macro in e.OldItems)
            {
                UnsubscribeMacro(macro);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (MacroDefinition macro in e.NewItems)
            {
                SubscribeMacro(macro);
            }
        }

        MacroHotkeysChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(Macros));
        RaiseMacroCommandStates();
    }

    private void SubscribeMacro(MacroDefinition macro)
    {
        macro.PropertyChanged += MacroPropertyChanged;
        macro.Steps.CollectionChanged += MacroStepsCollectionChanged;
        foreach (var step in macro.Steps)
        {
            step.PropertyChanged += MacroStepPropertyChanged;
        }
    }

    private void UnsubscribeMacro(MacroDefinition macro)
    {
        macro.PropertyChanged -= MacroPropertyChanged;
        macro.Steps.CollectionChanged -= MacroStepsCollectionChanged;
        foreach (var step in macro.Steps)
        {
            step.PropertyChanged -= MacroStepPropertyChanged;
        }
    }

    private void MacroPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MacroDefinition.Hotkey) or nameof(MacroDefinition.Enabled))
        {
            MacroHotkeysChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void MacroStepsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (MacroStep step in e.OldItems)
            {
                step.PropertyChanged -= MacroStepPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (MacroStep step in e.NewItems)
            {
                step.PropertyChanged += MacroStepPropertyChanged;
            }
        }

        RaiseMacroCommandStates();
    }

    private void MacroStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (SelectedMacro is not null)
        {
            SelectedMacro.LastError = string.Empty;
        }
    }

    private string GetMacroStepTypeName(MacroStepType type)
    {
        return type switch
        {
            MacroStepType.KeyPress => L["MacroStepKeyPress"],
            MacroStepType.Delay => L["MacroStepDelayAction"],
            MacroStepType.MouseClick => L["MacroStepMouseClick"],
            MacroStepType.MouseMove => L["MacroStepMouseMove"],
            MacroStepType.MouseWheel => L["MacroStepMouseWheel"],
            _ => L["MacroStepType"]
        };
    }

    private MacroValidationResult ValidateMacro(MacroDefinition macro)
    {
        return _macroValidator.Validate(macro, Macros, GetReservedHotkeys());
    }

    private IEnumerable<string> GetReservedHotkeys()
    {
        yield return Settings.ScreenTranslationHotkey;
        yield return Settings.ScreenTranslationCaptureHotkey;
        yield return Settings.ScreenTranslationRegionHotkey;
        yield return Settings.UiDimmingHotkey;
        if (Settings.StarCitizenHotkeysEnabled)
        {
            yield return Settings.StarCitizenServerChangeHotkey;
            yield return Settings.StarCitizenRespawnHotkey;
            yield return Settings.StarCitizenStutterHotkey;
        }
    }

    private void SaveValidMacros()
    {
        if (Macros.All(macro => ValidateMacro(macro).IsValid))
        {
            _macroStorage.Save(Macros);
        }
    }

    private void MacroExecutionChanged(object? sender, MacroExecutionChangedEventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            var macro = Macros.FirstOrDefault(item => item.Id == e.MacroId);
            if (macro is null)
            {
                return;
            }

            macro.RunState = e.State;
            macro.LastError = e.Error ?? string.Empty;
            MacroStatusText = e.State switch
            {
                MacroRunState.Running => L.Format("MacroRunning", macro.Name),
                MacroRunState.Stopping => L["MacroStopping"],
                MacroRunState.Error => L.Format("MacroFailedStatus", macro.Name, e.Error ?? string.Empty),
                _ => L["MacroReady"]
            };
            OnPropertyChanged(nameof(IsMacroRunning));
            RaiseMacroCommandStates();
        }));
    }

    private void RaiseMacroCommandStates()
    {
        RemoveMacroCommand.RaiseCanExecuteChanged();
        DuplicateMacroCommand.RaiseCanExecuteChanged();
        SaveMacroCommand.RaiseCanExecuteChanged();
        ClearMacroHotkeyCommand.RaiseCanExecuteChanged();
        AddMacroKeyStepCommand.RaiseCanExecuteChanged();
        AddMacroDelayStepCommand.RaiseCanExecuteChanged();
        AddMacroMouseClickStepCommand.RaiseCanExecuteChanged();
        AddMacroMouseMoveStepCommand.RaiseCanExecuteChanged();
        AddMacroMouseWheelStepCommand.RaiseCanExecuteChanged();
        RemoveMacroStepCommand.RaiseCanExecuteChanged();
        MoveMacroStepUpCommand.RaiseCanExecuteChanged();
        MoveMacroStepDownCommand.RaiseCanExecuteChanged();
        RunMacroCommand.RaiseCanExecuteChanged();
        StopMacroCommand.RaiseCanExecuteChanged();
    }

    private void RefreshDataStorage()
    {
        DataStorageItems.Clear();
        foreach (var item in _dataStorageService.GetItems())
        {
            DataStorageItems.Add(item);
        }

        OnPropertyChanged(nameof(DataStorageSummaryText));
        OnPropertyChanged(nameof(DataStoragePathText));
        OnPropertyChanged(nameof(DataRetentionSummaryText));
        OnPropertyChanged(nameof(DataRetentionHintText));
    }

    private void CleanupExpiredData()
    {
        var deleted = _dataStorageService.CleanupExpired();
        AddLog("Info", L.Format("LogDataRetentionCleanup", deleted));
        RefreshDataStorage();
    }

    private void DeleteWorkData(bool keepSettings)
    {
        var title = keepSettings ? L["DeleteWorkDataConfirmTitle"] : L["DeleteAllDataConfirmTitle"];
        var text = keepSettings ? L["DeleteWorkDataConfirmText"] : L["DeleteAllDataConfirmText"];
        if (ConfirmationRequested?.Invoke(title, text) != true)
        {
            return;
        }

        var deleted = keepSettings
            ? _dataStorageService.DeleteWorkData(keepSettings: true)
            : _dataStorageService.DeleteAllNonSettingsData();
        _learningService.Clear();
        AddLog("Warn", L.Format("LogDataStorageDeleted", deleted));
        StarCitizenSessions.Clear();
        RefreshDataStorage();
        OnPropertyChanged(nameof(DataStorageSummaryText));
        OnPropertyChanged(nameof(StarCitizenSessionTotalsText));
        OnPropertyChanged(nameof(StarCitizenSessionHealthText));
        OnPropertyChanged(nameof(StarCitizenRecentProblemText));
    }

    public void AddDiagnosticLog(string level, string message)
    {
        AddLog(level, message);
    }

    private void AddLog(string level, string message)
    {
        AddLog(level, message, null);
    }

    private void AddLog(string level, string message, IReadOnlyList<string>? details)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            Logs.Insert(0, new LogEntry
            {
                Level = level,
                Message = message,
                Details = details?.Where(detail => !string.IsNullOrWhiteSpace(detail)).ToList() ?? []
            });
            while (Logs.Count > 500)
            {
                Logs.RemoveAt(Logs.Count - 1);
            }

            OnPropertyChanged(nameof(LastLogText));
        });
    }

    public void PersistSettings()
    {
        SaveSettings();
    }

    private void SaveSettings()
    {
        _settingsService.Save(Settings);
    }

    public void SetHotkeyStatus(bool success, string details)
    {
        HotkeyStatusText = success
            ? L.Format("HotkeyRegistrationOk", details)
            : L.Format("HotkeyRegistrationFailed", details);
    }

    public void SetStarCitizenPath(string path)
    {
        Settings.StarCitizenPath = path;
        SaveSettings();
    }

    private static void AddHistory(ObservableCollection<double> collection, double value)
    {
        if (!IsFinite(value))
        {
            value = 0;
        }

        collection.Add(value);
        while (collection.Count > 3600)
        {
            collection.RemoveAt(0);
        }
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values)
    {
        collection.Clear();
        foreach (var value in values)
        {
            collection.Add(value);
        }
    }

    private static SystemSnapshot MergeWithPreviousProcesses(SystemSnapshot current, IReadOnlyList<ProcessSnapshot> previousProcesses)
    {
        return new SystemSnapshot
        {
            RamUsagePercent = current.RamUsagePercent,
            RamUsedGb = current.RamUsedGb,
            RamTotalGb = current.RamTotalGb,
            PagefileUsagePercent = current.PagefileUsagePercent,
            PagefileUsedGb = current.PagefileUsedGb,
            PagefileTotalGb = current.PagefileTotalGb,
            CpuUsagePercent = current.CpuUsagePercent,
            GpuUsagePercent = current.GpuUsagePercent,
            VramUsagePercent = current.VramUsagePercent,
            VramUsedGb = current.VramUsedGb,
            VramTotalGb = current.VramTotalGb,
            IsGpuDataAvailable = current.IsGpuDataAvailable,
            AverageFrametimeMs = current.AverageFrametimeMs,
            TemperatureCelsius = current.TemperatureCelsius,
            CpuTemperatureCelsius = current.CpuTemperatureCelsius,
            Cores = current.Cores,
            Processes = previousProcesses,
            ProcessDataUpdated = false
        };
    }

    private void UpdateMetricCards(SystemSnapshot snapshot)
    {
        Replace(MetricCards,
        [
            new MetricCard
            {
                Title = L["CpuLoad"],
                Value = CpuUsageText,
                Detail = L["TotalLoad"],
                State = PressureText(snapshot.CpuUsagePercent),
                Accent = BrushFromRgb(245, 158, 11),
                History = CpuHistory.ToList()
            },
            new MetricCard
            {
                Title = L["RamUsage"],
                Value = RamUsageText,
                Detail = RamGbText,
                State = PressureText(snapshot.RamUsagePercent),
                Accent = BrushFromRgb(34, 197, 94),
                History = RamHistory.ToList()
            },
            new MetricCard
            {
                Title = L["GpuLoad"],
                Value = GpuUsageText,
                Detail = snapshot.IsGpuDataAvailable ? L["WindowsCounter"] : L["NoGpuData"],
                State = snapshot.IsGpuDataAvailable ? PressureText(snapshot.GpuUsagePercent) : L["Unknown"],
                Accent = BrushFromRgb(168, 85, 247),
                History = GpuHistory.ToList()
            },
            new MetricCard
            {
                Title = L["VramUsage"],
                Value = VramUsageText,
                Detail = VramGbText,
                State = snapshot.IsGpuDataAvailable ? PressureText(snapshot.VramUsagePercent) : L["Unknown"],
                Accent = BrushFromRgb(59, 130, 246),
                History = VramHistory.ToList()
            },
            new MetricCard
            {
                Title = L["Frametime"],
                Value = FrametimeText,
                Detail = L["PassiveEstimate"],
                State = snapshot.AverageFrametimeMs > 22 ? L["Warning"] : L["Stable"],
                Accent = BrushFromRgb(249, 115, 22),
                History = FrametimeHistory.ToList()
            },
            new MetricCard
            {
                Title = L["GpuTemperature"],
                Value = GpuTemperatureText,
                Detail = L["GpuTemperature"],
                State = L["Unknown"],
                Accent = BrushFromRgb(239, 68, 68),
                History = []
            },
            new MetricCard
            {
                Title = L["CpuTemperature"],
                Value = CpuTemperatureText,
                Detail = L["CpuTemperature"],
                State = L["Unknown"],
                Accent = BrushFromRgb(245, 158, 11),
                History = []
            }
        ]);
    }

    private void RefreshNavigationItems()
    {
        Replace(NavigationItems, FeatureCatalog.All
            .Where(feature => IsFeatureVisible(feature.NavigationKey))
            .Select(feature => Nav(feature.NavigationKey, L[feature.TitleKey], feature.Icon)));
    }

    private NavigationItem Nav(string key, string title, string icon)
    {
        return new NavigationItem
        {
            Key = key,
            Title = title,
            Icon = icon,
            IsActive = CurrentView == key
        };
    }

    private void RefreshFeatureToggles()
    {
        Replace(FeatureToggles, FeatureCatalog.All
            .Where(feature => !feature.IsRequired)
            .Select(feature => new FeatureToggleViewModel(feature, L[feature.TitleKey], Settings, HandleFeatureToggled)));
    }

    private void HandleFeatureToggled()
    {
        RefreshNavigationItems();
        EnsureCurrentViewAvailable();
    }

    private void EnsureCurrentViewAvailable()
    {
        if (IsFeatureVisible(CurrentView))
        {
            return;
        }

        CurrentView = "Dashboard";
    }

    private bool IsFeatureVisible(string navigationKey)
    {
        var feature = FeatureCatalog.All.FirstOrDefault(item =>
            item.NavigationKey.Equals(navigationKey, StringComparison.Ordinal));
        return feature is null
            || feature.IsRequired
            || feature.SettingsPropertyName is null
            || Settings.GetFeatureEnabled(feature.SettingsPropertyName);
    }

    private void UpdateRecommendations(SystemSnapshot snapshot)
    {
        var selectedId = SelectedRecommendation?.Id;
        Replace(Recommendations, _learningService.Analyze(snapshot));
        if (selectedId is not null)
        {
            SelectedRecommendation = Recommendations.FirstOrDefault(recommendation => recommendation.Id == selectedId)
                ?? SelectedRecommendation;
        }
    }

    private void UpdateProtectedProcesses(SystemSnapshot snapshot)
    {
        var protectedRows = snapshot.Processes
            .Where(process => process.IsProtected || process.IsStarCitizen)
            .OrderByDescending(process => process.IsStarCitizen)
            .ThenBy(process => process.Name)
            .Take(8)
            .Select(process => new ProtectedProcessView
            {
                Name = process.Name,
                Priority = process.Priority,
                Status = process.IsProtected ? L["Protected"] : L["NotProtected"]
            })
            .ToList();

        if (protectedRows.Count == 0)
        {
            protectedRows.AddRange(Settings.ProtectionRules
                .Where(rule => rule.Enabled)
                .Take(5)
                .Select(rule => new ProtectedProcessView
                {
                    Name = rule.Pattern,
                    Priority = "-",
                    Status = L["Rule"]
                }));
        }

        Replace(ProtectedProcesses, protectedRows);
    }

    private void UpdateStarCitizenSession(SystemSnapshot snapshot)
    {
        var starCitizen = snapshot.Processes.FirstOrDefault(process => process.IsStarCitizen);
        if (starCitizen is not null && _starCitizenSessionStarted is null)
        {
            _starCitizenMissingSince = null;
            AutoDetectStarCitizenPath(starCitizen);
            _starCitizenSessionStarted = DateTime.UtcNow;
            _starCitizenSessionBaseline = CreateBaseline(snapshot, starCitizen);
            _starCitizenSessionPeak = _starCitizenSessionBaseline;
            _lastMarkedEventBaseline = _starCitizenSessionBaseline;
            _starCitizenSessionLogOffset = _starCitizenLogService.GetCurrentLogLength(Settings.StarCitizenPath);
            _lastStarCitizenExitText = string.Empty;
            _lastStarCitizenExitEvidence = [];
            ResetStarCitizenAutoDetection();
            _starCitizenAutoStutterCount = 0;
            _starCitizenPressureSpikeCount = 0;
            _starCitizenManualEventCount = 0;
            StarCitizenEvents.Clear();
            OnPropertyChanged(nameof(LastStarCitizenExitText));
        }
        else if (starCitizen is not null && _starCitizenSessionBaseline is null)
        {
            _starCitizenMissingSince = null;
            _starCitizenSessionBaseline = CreateBaseline(snapshot, starCitizen);
            _starCitizenSessionPeak = _starCitizenSessionBaseline;
            _lastMarkedEventBaseline ??= _starCitizenSessionBaseline;
        }
        else if (starCitizen is not null && _starCitizenSessionPeak is not null)
        {
            _starCitizenMissingSince = null;
            _starCitizenSessionPeak = MaxBaseline(_starCitizenSessionPeak, CreateBaseline(snapshot, starCitizen));
            MaybeDetectStarCitizenStutter(snapshot, starCitizen);
        }
        else if (starCitizen is null && _starCitizenSessionStarted is not null)
        {
            _starCitizenMissingSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - _starCitizenMissingSince.Value < TimeSpan.FromSeconds(12))
            {
                return;
            }

            var sessionStarted = _starCitizenSessionStarted.Value;
            var sessionEnded = _starCitizenMissingSince.Value;
            _lastStarCitizenSessionDuration = sessionEnded - sessionStarted;
            AnalyzeStarCitizenExit(sessionStarted, sessionEnded);
            AddStarCitizenSession(sessionStarted, sessionEnded);
            _starCitizenSessionStarted = null;
            _starCitizenMissingSince = null;
            _starCitizenSessionBaseline = null;
            _starCitizenSessionPeak = null;
            _lastMarkedEventBaseline = null;
            _starCitizenSessionLogOffset = 0;
        }
    }

    private void AutoDetectStarCitizenPath(ProcessSnapshot starCitizen)
    {
        if (!string.IsNullOrWhiteSpace(Settings.StarCitizenPath)
            || string.IsNullOrWhiteSpace(starCitizen.ExecutablePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(starCitizen.ExecutablePath);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "Game.log"))
                || Directory.Exists(Path.Combine(directory, "logbackups")))
            {
                Settings.StarCitizenPath = directory;
                OnPropertyChanged(nameof(StarCitizenLogPathText));
            ScanShaderCache();
                AddLog("Info", L.Format("LogStarCitizenPathDetected", directory));
                return;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }
    }


    private void MaybeDetectStarCitizenStutter(SystemSnapshot snapshot, ProcessSnapshot starCitizen)
    {
        if (_starCitizenSessionStarted is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var current = CreateBaseline(snapshot, starCitizen);
        var baseline = _starCitizenSessionBaseline ?? current;
        _lastAutoDetectionBaseline ??= baseline;

        TrackStarCitizenFrametime(snapshot.AverageFrametimeMs);
        DetectStarCitizenFrametimeStutter(snapshot, current, now);
        DetectStarCitizenMemoryPressure(snapshot, starCitizen, current, now);
        DetectStarCitizenCpuPressure(snapshot, current, now);
        _lastAutoDetectionBaseline = current;
    }

    private void ResetStarCitizenAutoDetection()
    {
        _lastAutoStutterDetected = DateTime.MinValue;
        _lastAutoPressureDetected = DateTime.MinValue;
        _lastAutoCpuPressureDetected = DateTime.MinValue;
        _starCitizenFrametimeWindow.Clear();
        _starCitizenConsecutiveStutterSamples = 0;
        _starCitizenConsecutiveMemoryPressureSamples = 0;
        _starCitizenConsecutiveCpuPressureSamples = 0;
        _lastAutoDetectionBaseline = null;
    }

    private void TrackStarCitizenFrametime(double frametimeMs)
    {
        if (!IsFinite(frametimeMs) || frametimeMs <= 0)
        {
            return;
        }

        _starCitizenFrametimeWindow.Enqueue(frametimeMs);
        while (_starCitizenFrametimeWindow.Count > 20)
        {
            _starCitizenFrametimeWindow.Dequeue();
        }
    }

    private void DetectStarCitizenFrametimeStutter(SystemSnapshot snapshot, SessionBaseline current, DateTime now)
    {
        var frametime = snapshot.AverageFrametimeMs;
        if (!IsFinite(frametime) || frametime <= 0)
        {
            _starCitizenConsecutiveStutterSamples = 0;
            return;
        }

        var rollingAverage = _starCitizenFrametimeWindow.Count == 0
            ? frametime
            : _starCitizenFrametimeWindow.Average();
        var dynamicSpikeLimit = Math.Max(80, rollingAverage * 2.2);
        var isHeavySpike = frametime >= 150;
        var isSpike = frametime >= dynamicSpikeLimit || isHeavySpike;

        _starCitizenConsecutiveStutterSamples = isSpike
            ? _starCitizenConsecutiveStutterSamples + 1
            : 0;

        var confirmed = isHeavySpike || _starCitizenConsecutiveStutterSamples >= 2;
        if (!confirmed || now - _lastAutoStutterDetected <= TimeSpan.FromSeconds(35))
        {
            return;
        }

        _starCitizenAutoStutterCount++;
        AddStarCitizenEvent(
            "Auto Stutter",
            $"Auto-detected frametime spike: {frametime:0.0} ms (rolling {rollingAverage:0.0} ms) · {FormatBaselineDelta(_lastMarkedEventBaseline ?? _starCitizenSessionBaseline ?? current, current)}");
        _lastAutoStutterDetected = now;
        _starCitizenConsecutiveStutterSamples = 0;
    }

    private void DetectStarCitizenMemoryPressure(SystemSnapshot snapshot, ProcessSnapshot starCitizen, SessionBaseline current, DateTime now)
    {
        var vramHigh = snapshot.IsGpuDataAvailable && snapshot.VramUsagePercent >= 94;
        var ramHigh = snapshot.RamUsagePercent >= 92;
        var lowFreeRam = snapshot.RamTotalGb > 0 && snapshot.RamTotalGb - snapshot.RamUsedGb <= 2;
        var commitHigh = starCitizen.CommitMb >= 28000;
        var previous = _lastAutoDetectionBaseline ?? _starCitizenSessionBaseline ?? current;
        var commitJump = current.StarCitizenCommitMb - previous.StarCitizenCommitMb >= 768;
        var ramJump = current.SystemRamGb - previous.SystemRamGb >= 1.25;
        var vramJump = snapshot.IsGpuDataAvailable && current.VramGb - previous.VramGb >= 0.75;
        var suddenGrowth = commitJump || (ramJump && vramJump);
        var pressure = ramHigh || lowFreeRam || vramHigh || commitHigh || suddenGrowth;

        _starCitizenConsecutiveMemoryPressureSamples = pressure
            ? _starCitizenConsecutiveMemoryPressureSamples + 1
            : 0;

        if (_starCitizenConsecutiveMemoryPressureSamples < 4
            || now - _lastAutoPressureDetected <= TimeSpan.FromMinutes(2))
        {
            return;
        }

        _starCitizenPressureSpikeCount++;
        AddStarCitizenEvent(
            "Pressure Spike",
            $"Auto-detected memory pressure: RAM {snapshot.RamUsagePercent:0}% ({snapshot.RamUsedGb:0.0}/{snapshot.RamTotalGb:0.0} GB) · VRAM {VramPressureText} · SC commit {starCitizen.CommitMb:0} MB");
        _lastAutoPressureDetected = now;
        _starCitizenConsecutiveMemoryPressureSamples = 0;
    }

    private void DetectStarCitizenCpuPressure(SystemSnapshot snapshot, SessionBaseline current, DateTime now)
    {
        var cpuSaturated = snapshot.CpuUsagePercent >= 95;
        var gpuWaiting = !snapshot.IsGpuDataAvailable || snapshot.GpuUsagePercent <= 75;
        var pressure = cpuSaturated && gpuWaiting;

        _starCitizenConsecutiveCpuPressureSamples = pressure
            ? _starCitizenConsecutiveCpuPressureSamples + 1
            : 0;

        if (_starCitizenConsecutiveCpuPressureSamples < 8
            || now - _lastAutoCpuPressureDetected <= TimeSpan.FromMinutes(2))
        {
            return;
        }

        _starCitizenPressureSpikeCount++;
        AddStarCitizenEvent(
            "CPU Pressure",
            $"Auto-detected CPU saturation: CPU {snapshot.CpuUsagePercent:0}% · GPU {(snapshot.IsGpuDataAvailable ? snapshot.GpuUsagePercent.ToString("0") + "%" : "n/a")} · {FormatBaselineDelta(_lastMarkedEventBaseline ?? _starCitizenSessionBaseline ?? current, current)}");
        _lastAutoCpuPressureDetected = now;
        _starCitizenConsecutiveCpuPressureSamples = 0;
    }

    private void AddStarCitizenEvent(string eventType, string summary)
    {
        StarCitizenEvents.Insert(0, new StarCitizenEventView
        {
            Type = eventType,
            SessionTime = SessionTimeText,
            Summary = summary
        });

        while (StarCitizenEvents.Count > 12)
        {
            StarCitizenEvents.RemoveAt(StarCitizenEvents.Count - 1);
        }

        AddLog("Info", $"Star Citizen {eventType}: {summary}");
        OnPropertyChanged(nameof(StarCitizenEventDeltaText));
        OnPropertyChanged(nameof(StarCitizenRiskText));
        OnPropertyChanged(nameof(StarCitizenAutoDetectionText));
    }

    private void AnalyzeStarCitizenExit(DateTime sessionStartedUtc, DateTime sessionEndedUtc)
    {
        var analysis = _starCitizenLogService.AnalyzeExit(
            Settings.StarCitizenPath,
            _starCitizenSessionLogOffset,
            sessionStartedUtc,
            sessionEndedUtc);
        var evidence = analysis.Evidence.Count == 0 ? L["StarCitizenExitNoEvidence"] : string.Join(" | ", analysis.Evidence.Take(3));
        _lastStarCitizenExitText = L.Format("StarCitizenExitShortSummary", L[analysis.StatusKey]);
        _lastStarCitizenExitEvidence = analysis.Evidence;

        if (!string.IsNullOrWhiteSpace(analysis.LogPath) && string.IsNullOrWhiteSpace(Settings.StarCitizenPath))
        {
            Settings.StarCitizenPath = Path.GetDirectoryName(analysis.LogPath) ?? string.Empty;
        }

        AddLog("Info", L.Format("LogStarCitizenExitAnalyzed", L[analysis.StatusKey], evidence));
        OnPropertyChanged(nameof(LastStarCitizenExitText));
        OnPropertyChanged(nameof(StarCitizenLogPathText));
    }

    private void AddStarCitizenSession(DateTime started, DateTime ended)
    {
        var peak = _starCitizenSessionPeak;
        var evidence = _lastStarCitizenExitEvidence.Take(8).ToList();
        StarCitizenSessions.Insert(0, new StarCitizenSessionView
        {
            StartedUtc = started,
            EndedUtc = ended,
            Started = started.ToLocalTime(),
            Ended = ended.ToLocalTime(),
            StartedText = started.ToLocalTime().ToString("g"),
            EndedText = ended.ToLocalTime().ToString("g"),
            DurationText = _lastStarCitizenSessionDuration.ToString(@"hh\:mm\:ss"),
            PeakSummary = peak is null
                ? L["Unknown"]
                : L.Format(
                    "StarCitizenSessionPeakSummary",
                    peak.SystemRamGb,
                    peak.PagefileGb,
                    peak.VramGb,
                    peak.StarCitizenRamMb,
                    peak.StarCitizenCommitMb),
            AutoStutterCount = _starCitizenAutoStutterCount,
            PressureSpikeCount = _starCitizenPressureSpikeCount,
            ManualEventCount = _starCitizenManualEventCount,
            ExitSummary = _lastStarCitizenExitText,
            LogEvidenceSummary = evidence.Count == 0
                ? L["StarCitizenExitNoEvidence"]
                : string.Join(Environment.NewLine, evidence),
            LogEvidence = evidence
        });

        while (StarCitizenSessions.Count > 250)
        {
            StarCitizenSessions.RemoveAt(StarCitizenSessions.Count - 1);
        }

        SaveStarCitizenSessions();
        OnPropertyChanged(nameof(StarCitizenSessionTotalsText));
        OnPropertyChanged(nameof(StarCitizenSessionHealthText));
        OnPropertyChanged(nameof(StarCitizenRecentProblemText));
    }

    private void LoadStarCitizenSessions()
    {
        var sessions = _starCitizenSessionHistoryService.Load().ToList();
        sessions = RepairKnownBadStarCitizenSessions(sessions);
        sessions = MergeLogDiscoveredStarCitizenSessions(sessions);
        Replace(StarCitizenSessions, sessions);
        SaveStarCitizenSessions();
        OnPropertyChanged(nameof(StarCitizenSessionTotalsText));
        OnPropertyChanged(nameof(StarCitizenSessionHealthText));
        OnPropertyChanged(nameof(StarCitizenRecentProblemText));
    }

    private List<StarCitizenSessionView> MergeLogDiscoveredStarCitizenSessions(List<StarCitizenSessionView> sessions)
    {
        var importSince = sessions.Count == 0
            ? DateTime.UtcNow.AddDays(-14)
            : sessions.Min(session => session.StartedUtc).AddDays(-1);
        var discovered = _starCitizenLogService.DiscoverSessions(Settings.StarCitizenPath, importSince);
        var added = 0;

        foreach (var session in discovered)
        {
            if (sessions.Any(existing => IsSameStarCitizenSession(existing, session)))
            {
                continue;
            }

            RememberStarCitizenLogPath(session.LogPath);
            var evidence = session.Evidence.Take(8).ToList();
            sessions.Add(new StarCitizenSessionView
            {
                StartedUtc = session.StartedUtc,
                EndedUtc = session.EndedUtc,
                Started = session.StartedUtc.ToLocalTime(),
                Ended = session.EndedUtc.ToLocalTime(),
                StartedText = session.StartedUtc.ToLocalTime().ToString("g"),
                EndedText = session.EndedUtc.ToLocalTime().ToString("g"),
                DurationText = (session.EndedUtc - session.StartedUtc).ToString(@"hh\:mm\:ss"),
                PeakSummary = L["StarCitizenSessionLogOnlyPeak"],
                ExitSummary = L.Format("StarCitizenExitShortSummary", L[session.StatusKey]),
                LogEvidenceSummary = evidence.Count == 0
                    ? L["StarCitizenExitNoEvidence"]
                    : string.Join(Environment.NewLine, evidence),
                LogEvidence = evidence
            });
            added++;
        }

        if (added > 0)
        {
            AddLog("Info", L.Format("LogStarCitizenSessionsImported", added));
        }

        return sessions
            .OrderByDescending(session => session.StartedUtc)
            .Take(250)
            .ToList();
    }

    private static bool IsSameStarCitizenSession(StarCitizenSessionView existing, StarCitizenLogSession discovered)
    {
        return Math.Abs((existing.StartedUtc - discovered.StartedUtc).TotalMinutes) <= 3
            || Math.Abs((existing.EndedUtc - discovered.EndedUtc).TotalMinutes) <= 3;
    }

    private List<StarCitizenSessionView> RepairKnownBadStarCitizenSessions(List<StarCitizenSessionView> sessions)
    {
        var repaired = 0;
        var result = sessions
            .Select(session =>
            {
                if (!ShouldRepairKnownBadStarCitizenSession(session))
                {
                    return session;
                }

                var analysis = _starCitizenLogService.AnalyzeExit(
                    Settings.StarCitizenPath,
                    sessionLogOffset: 0,
                    session.StartedUtc,
                    session.EndedUtc);
                if (!IsBetterStarCitizenExitAnalysis(session, analysis))
                {
                    return session;
                }

                RememberStarCitizenLogPath(analysis.LogPath);
                repaired++;
                return WithRepairedStarCitizenExit(session, analysis);
            })
            .ToList();

        if (repaired > 0)
        {
            AddLog("Info", $"Repaired {repaired} Star Citizen session log detail entries from available logs.");
        }

        return result;
    }

    private void RememberStarCitizenLogPath(string logPath)
    {
        if (!string.IsNullOrWhiteSpace(Settings.StarCitizenPath)
            || string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(logPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        if (Path.GetFileName(directory).Equals("logbackups", StringComparison.OrdinalIgnoreCase))
        {
            directory = Path.GetDirectoryName(directory);
        }

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Settings.StarCitizenPath = directory;
            OnPropertyChanged(nameof(StarCitizenLogPathText));
        }
    }

    private static bool ShouldRepairKnownBadStarCitizenSession(StarCitizenSessionView session)
    {
        if (session.StartedUtc == default || session.EndedUtc == default)
        {
            return false;
        }

        var text = $"{session.ExitSummary} {session.LogEvidenceSummary}";
        var hasWeakMissingEvidence = text.Contains("no notable log entry", StringComparison.OrdinalIgnoreCase)
            || text.Contains("kein auff", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cause unclear", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Ursache unklar", StringComparison.OrdinalIgnoreCase);
        var hasOnlyCrashArtifacts = session.LogEvidence.Count > 0
            && session.LogEvidence.Any(IsCrashArtifactEvidence)
            && !session.LogEvidence.Any(IsStrongCrashLogEvidence);

        return hasWeakMissingEvidence || hasOnlyCrashArtifacts;
    }

    private static bool IsBetterStarCitizenExitAnalysis(StarCitizenSessionView session, StarCitizenExitAnalysis analysis)
    {
        if (analysis.StatusKey == "StarCitizenExitLogMissing" || analysis.Evidence.Count == 0)
        {
            return false;
        }

        var newHasStrongEvidence = analysis.Evidence.Any(IsStrongCrashLogEvidence)
            || analysis.Evidence.Any(line => line.StartsWith("[") && !line.Contains("Crash artifact:", StringComparison.OrdinalIgnoreCase));
        if (!newHasStrongEvidence && analysis.StatusKey == "StarCitizenExitUnknown")
        {
            return false;
        }

        var oldText = $"{session.ExitSummary}\n{session.LogEvidenceSummary}";
        var newText = $"{analysis.StatusKey}\n{string.Join(Environment.NewLine, analysis.Evidence)}";
        return !oldText.Equals(newText, StringComparison.OrdinalIgnoreCase);
    }

    private StarCitizenSessionView WithRepairedStarCitizenExit(StarCitizenSessionView session, StarCitizenExitAnalysis analysis)
    {
        var evidence = analysis.Evidence.Take(8).ToList();
        return new StarCitizenSessionView
        {
            StartedUtc = session.StartedUtc,
            EndedUtc = session.EndedUtc,
            Started = session.Started,
            Ended = session.Ended,
            StartedText = session.StartedText,
            EndedText = session.EndedText,
            DurationText = session.DurationText,
            PeakSummary = session.PeakSummary,
            AutoStutterCount = session.AutoStutterCount,
            PressureSpikeCount = session.PressureSpikeCount,
            ManualEventCount = session.ManualEventCount,
            ExitSummary = L.Format("StarCitizenExitShortSummary", L[analysis.StatusKey]),
            LogEvidenceSummary = evidence.Count == 0
                ? session.LogEvidenceSummary
                : string.Join(Environment.NewLine, evidence),
            LogEvidence = evidence.Count == 0 ? session.LogEvidence : evidence
        };
    }

    private StarCitizenSessionView RefreshSessionLogEvidence(StarCitizenSessionView session)
    {
        if (session.StartedUtc == default
            || session.EndedUtc == default
            || IsSessionLogEvidenceInWindow(session))
        {
            return session;
        }

        var analysis = _starCitizenLogService.AnalyzeExit(
            Settings.StarCitizenPath,
            sessionLogOffset: 0,
            session.StartedUtc,
            session.EndedUtc);
        var evidence = analysis.Evidence.Take(8).ToList();
        return new StarCitizenSessionView
        {
            StartedUtc = session.StartedUtc,
            EndedUtc = session.EndedUtc,
            Started = session.Started,
            Ended = session.Ended,
            StartedText = session.StartedText,
            EndedText = session.EndedText,
            DurationText = session.DurationText,
            PeakSummary = session.PeakSummary,
            AutoStutterCount = session.AutoStutterCount,
            PressureSpikeCount = session.PressureSpikeCount,
            ManualEventCount = session.ManualEventCount,
            ExitSummary = L.Format("StarCitizenExitShortSummary", L[analysis.StatusKey]),
            LogEvidenceSummary = evidence.Count == 0
                ? L["StarCitizenExitNoEvidence"]
                : string.Join(Environment.NewLine, evidence),
            LogEvidence = evidence
        };
    }

    private static bool IsSessionLogEvidenceInWindow(StarCitizenSessionView session)
    {
        if (session.LogEvidence.Count == 0)
        {
            return false;
        }

        if (session.LogEvidence.Any(line => line.StartsWith('<')))
        {
            return false;
        }

        var evidenceText = $"{session.ExitSummary} {session.LogEvidenceSummary}";
        if (evidenceText.Contains("no notable log entry", StringComparison.OrdinalIgnoreCase)
            || evidenceText.Contains("kein auffälliger Log-Eintrag", StringComparison.OrdinalIgnoreCase)
            || evidenceText.Contains("cause unclear", StringComparison.OrdinalIgnoreCase)
            || evidenceText.Contains("Ursache unklar", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (session.LogEvidence.Any(IsCrashArtifactEvidence)
            && !session.LogEvidence.Any(IsStrongCrashLogEvidence))
        {
            return false;
        }

        var windowStart = session.StartedUtc.AddSeconds(-30);
        var windowEnd = session.EndedUtc.AddSeconds(30);
        var timestamps = session.LogEvidence
            .Select(TryReadLogTimestamp)
            .Where(timestamp => timestamp is not null)
            .Select(timestamp => timestamp!.Value)
            .ToList();

        return timestamps.Count > 0
            && timestamps.All(timestamp => timestamp >= windowStart && timestamp <= windowEnd);
    }

    private static bool IsCrashArtifactEvidence(string line)
    {
        return line.StartsWith("Crash artifact:", StringComparison.OrdinalIgnoreCase)
            || line.Contains(@"Star Citizen\Crashes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStrongCrashLogEvidence(string line)
    {
        return line.Contains("crash handler", StringComparison.OrdinalIgnoreCase)
            || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
            || line.Contains("access violation", StringComparison.OrdinalIgnoreCase)
            || line.Contains("saved dump file", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime? TryReadLogTimestamp(string line)
    {
        if (line.StartsWith('['))
        {
            var localEnd = line.IndexOf(']');
            if (localEnd <= 1)
            {
                return null;
            }

            return DateTime.TryParse(
                line[1..localEnd],
                null,
                System.Globalization.DateTimeStyles.AssumeLocal,
                out var localTimestamp)
                ? localTimestamp.ToUniversalTime()
                : null;
        }

        if (!line.StartsWith('<'))
        {
            return null;
        }

        var end = line.IndexOf('>');
        if (end <= 1)
        {
            return null;
        }

        return DateTime.TryParse(
            line[1..end],
            null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var timestamp)
            ? timestamp.ToUniversalTime()
            : null;
    }

    private void SaveStarCitizenSessions()
    {
        try
        {
            _starCitizenSessionHistoryService.Save(StarCitizenSessions);
        }
        catch (Exception ex)
        {
            AddLog("Warn", L.Format("LogStarCitizenSessionHistorySaveFailed", ex.Message));
        }
    }

    private void MarkStarCitizenEvent(string eventType)
    {
        var snapshot = _lastSnapshot;
        var starCitizen = snapshot?.Processes.FirstOrDefault(process => process.IsStarCitizen);
        if (snapshot is null || starCitizen is null)
        {
            AddLog("Warn", L["LogStarCitizenEventNoSession"]);
            return;
        }

        var current = CreateBaseline(snapshot, starCitizen);
        var previous = _lastMarkedEventBaseline ?? _starCitizenSessionBaseline ?? current;
        var summary = FormatBaselineDelta(previous, current);
        _starCitizenManualEventCount++;
        AddStarCitizenEvent(eventType, summary);
        _lastMarkedEventBaseline = current;
    }

    private CoreMetric LocalizeCore(CoreMetric core)
    {
        return new CoreMetric
        {
            Index = core.Index,
            UsagePercent = core.UsagePercent,
            CurrentMhz = core.CurrentMhz,
            MaxMhz = core.MaxMhz,
            ParkingState = L[core.ParkingState]
        };
    }

    private ProcessSnapshot LocalizeProcess(ProcessSnapshot process)
    {
        return new ProcessSnapshot
        {
            Id = process.Id,
            Name = process.Name,
            ExecutableName = process.ExecutableName,
            ExecutablePath = process.ExecutablePath,
            CpuPercent = process.CpuPercent,
            GpuPercent = process.GpuPercent,
            MemoryMb = process.MemoryMb,
            CommitMb = process.CommitMb,
            VramMb = process.VramMb,
            AiScore = CalculateAiScore(process),
            Priority = process.Priority,
            Status = L[process.Status],
            WindowTitle = process.WindowTitle,
            IsProtected = process.IsProtected,
            IsCritical = process.IsCritical,
            IsStarCitizen = process.IsStarCitizen,
            IsBackground = process.IsBackground
        };
    }

    private static int CalculateAiScore(ProcessSnapshot process)
    {
        if (process.IsProtected || process.IsCritical)
        {
            return 0;
        }

        var score = process.CpuPercent * 3
            + Math.Max(0, process.MemoryMb) / 64
            + Math.Max(0, process.CommitMb) / 128
            + Math.Max(0, process.VramMb) / 128;
        return (int)Math.Clamp(score, 0, 100);
    }

    private string BuildStarCitizenSessionSummary()
    {
        var snapshot = _lastSnapshot;
        var starCitizen = snapshot?.Processes.FirstOrDefault(process => process.IsStarCitizen);
        if (snapshot is null || starCitizen is null || _starCitizenSessionBaseline is null)
        {
            return L.Format("LastSessionValue", LastSessionTimeText);
        }

        var current = CreateBaseline(snapshot, starCitizen);
        return L.Format(
            "StarCitizenSessionSummary",
            current.StarCitizenRamMb,
            current.StarCitizenCommitMb,
            current.SystemRamGb,
            current.PagefileGb,
            current.VramGb,
            current.StarCitizenRamMb - _starCitizenSessionBaseline.StarCitizenRamMb,
            current.StarCitizenCommitMb - _starCitizenSessionBaseline.StarCitizenCommitMb,
            current.VramGb - _starCitizenSessionBaseline.VramGb);
    }

    private string BuildStarCitizenEventDelta()
    {
        var snapshot = _lastSnapshot;
        var starCitizen = snapshot?.Processes.FirstOrDefault(process => process.IsStarCitizen);
        if (snapshot is null || starCitizen is null || _lastMarkedEventBaseline is null)
        {
            return L["NoMarkedEvent"];
        }

        return FormatBaselineDelta(_lastMarkedEventBaseline, CreateBaseline(snapshot, starCitizen));
    }

    private string BuildStarCitizenSessionHealthText()
    {
        var completed = StarCitizenSessions.ToList();
        if (completed.Count == 0 && _starCitizenSessionStarted is null)
        {
            return L["NoTrackedStarCitizenSessions"];
        }

        var problematic = completed.Count(IsProblematicStarCitizenSession);
        var healthy = completed.Count - problematic;
        var stutters = completed.Sum(session => session.AutoStutterCount) + (_starCitizenSessionStarted is null ? 0 : _starCitizenAutoStutterCount);
        var pressure = completed.Sum(session => session.PressureSpikeCount) + (_starCitizenSessionStarted is null ? 0 : _starCitizenPressureSpikeCount);
        var active = _starCitizenSessionStarted is null
            ? L["Inactive"]
            : L.Format("ActiveSessionDuration", SessionTimeText);

        return L.Format(
            "StarCitizenSessionHealth",
            completed.Count,
            active,
            healthy,
            problematic,
            stutters,
            pressure);
    }

    private string BuildStarCitizenRecentProblemText()
    {
        var recent = StarCitizenSessions.Take(10).ToList();
        var problem = recent.FirstOrDefault(IsProblematicStarCitizenSession);
        if (problem is null)
        {
            return recent.Count == 0
                ? L["SessionHistoryAfterClose"]
                : L.Format("RecentSessionsNoCrash", recent.Count);
        }

        return L.Format("LatestSessionIssue", problem.StartedText, problem.ExitDisplaySummary);
    }

    private string BuildStarCitizenAutoDetectionText()
    {
        if (_starCitizenSessionStarted is null)
        {
            return L["AutoDetectionArmed"];
        }

        var unavailable = L["NotAvailableShort"];
        var frametime = _lastSnapshot is null ? unavailable : $"{_lastSnapshot.AverageFrametimeMs:0.0} ms";
        var rolling = _starCitizenFrametimeWindow.Count == 0
            ? unavailable
            : L.Format("AverageMilliseconds", _starCitizenFrametimeWindow.Average());
        var ram = _lastSnapshot is null ? unavailable : $"{_lastSnapshot.RamUsagePercent:0}%";
        var vram = _lastSnapshot is null || !_lastSnapshot.IsGpuDataAvailable ? unavailable : $"{_lastSnapshot.VramUsagePercent:0}%";
        return L.Format(
            "AutoDetectionStatus",
            _starCitizenAutoStutterCount,
            _starCitizenPressureSpikeCount,
            frametime,
            rolling,
            ram,
            vram);
    }

    private static bool IsProblematicStarCitizenSession(StarCitizenSessionView session)
    {
        var text = $"{session.ExitSummary} {session.LogEvidenceSummary}";
        return session.AutoStutterCount >= 3
            || session.PressureSpikeCount >= 2
            || text.Contains("crash", StringComparison.OrdinalIgnoreCase)
            || text.Contains("error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("30k", StringComparison.OrdinalIgnoreCase)
            || text.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
            || text.Contains("memory allocation", StringComparison.OrdinalIgnoreCase)
            || text.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildStarCitizenSessionTotals()
    {
        var completedDuration = StarCitizenSessions.Aggregate(
            TimeSpan.Zero,
            (total, session) => total + CalculateSessionDuration(session));
        var activeDuration = _starCitizenSessionStarted is null
            ? TimeSpan.Zero
            : DateTime.UtcNow - _starCitizenSessionStarted.Value;
        var totalDuration = completedDuration + activeDuration;
        var sessionCount = StarCitizenSessions.Count + (_starCitizenSessionStarted is null ? 0 : 1);

        return L.Format(
            "StarCitizenSessionTotals",
            sessionCount,
            totalDuration.ToString(@"hh\:mm\:ss"),
            StarCitizenSessions.Count,
            _starCitizenSessionStarted is null ? L["Off"] : L["On"]);
    }

    private static TimeSpan CalculateSessionDuration(StarCitizenSessionView session)
    {
        if (session.StartedUtc != default && session.EndedUtc != default)
        {
            return session.EndedUtc - session.StartedUtc;
        }

        return session.Ended - session.Started;
    }

    private string BuildDefenderHint()
    {
        var snapshot = _lastSnapshot;
        if (snapshot?.Processes.Any(process => process.IsStarCitizen) != true)
        {
            return L["DefenderHintNoSession"];
        }

        var securityProcesses = snapshot.Processes
            .Select(process => new
            {
                Process = process,
                ProductName = SecurityProductName(process.Name)
            })
            .Where(item => item.ProductName is not null)
            .ToList();
        if (securityProcesses.Count == 0)
        {
            return L["DefenderHintIdle"];
        }

        var securityProcess = securityProcesses
            .OrderByDescending(item => item.Process.CpuPercent)
            .ThenByDescending(item => item.Process.CommitMb)
            .First();
        var productNames = securityProcesses
            .Select(item => item.ProductName ?? L["Unknown"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var productSummary = string.Join(", ", productNames.Take(4));
        if (productNames.Count > 4)
        {
            productSummary += ", +" + (productNames.Count - 4);
        }

        var process = securityProcess.Process;
        var productName = securityProcess.ProductName ?? L["Unknown"];
        if (productNames.Count > 1)
        {
            return L.Format("SecurityHintMultiple", productSummary, productName, process.CpuPercent, process.MemoryMb, process.CommitMb);
        }

        if (process.CpuPercent >= 2 || process.MemoryMb >= 180 || process.CommitMb >= 250)
        {
            return L.Format("DefenderHintActive", productName, process.CpuPercent, process.MemoryMb, process.CommitMb);
        }

        return L.Format("DefenderHintLow", productName, process.CpuPercent, process.MemoryMb);
    }

    private int CalculateStarCitizenRiskScore()
    {
        var snapshot = _lastSnapshot;
        var score = 0d;
        if (snapshot is null || snapshot.Processes.Any(process => process.IsStarCitizen) != true)
        {
            return 0;
        }

        score += Math.Max(0, snapshot.RamUsagePercent - 70) * 1.1;
        score += Math.Max(0, snapshot.PagefileUsagePercent - 65) * 0.9;
        score += Math.Max(0, snapshot.VramUsagePercent - 75) * 1.2;
        score += Math.Max(0, snapshot.CpuUsagePercent - 80) * 0.8;

        var securityProcess = snapshot.Processes.FirstOrDefault(process => SecurityProductName(process.Name) is not null);
        if (securityProcess?.CpuPercent >= 2)
        {
            score += 8;
        }

        if (_starCitizenSessionStarted is not null && (DateTime.UtcNow - _starCitizenSessionStarted.Value) > TimeSpan.FromHours(3))
        {
            score += 10;
        }

        if (_starCitizenSessionBaseline is not null)
        {
            var current = CreateBaseline(snapshot, snapshot.Processes.First(process => process.IsStarCitizen));
            if (current.StarCitizenCommitMb - _starCitizenSessionBaseline.StarCitizenCommitMb > 2048)
            {
                score += 12;
            }
        }

        return (int)Math.Clamp(score, 0, 100);
    }

    private string BuildStarCitizenRiskReason()
    {
        var snapshot = _lastSnapshot;
        if (snapshot?.Processes.Any(process => process.IsStarCitizen) != true)
        {
            return L["StarCitizenNotDetected"];
        }

        var reasons = new List<string>();
        if (snapshot.CpuUsagePercent >= 90)
        {
            reasons.Add(L["RiskReasonCpu"]);
        }

        if (snapshot.RamUsagePercent >= 85)
        {
            reasons.Add(L["RiskReasonRam"]);
        }

        if (snapshot.PagefileUsagePercent >= 80)
        {
            reasons.Add(L["RiskReasonCommit"]);
        }

        if (snapshot.VramUsagePercent >= 85)
        {
            reasons.Add(L["RiskReasonVram"]);
        }

        if (snapshot.Processes.Any(process => SecurityProductName(process.Name) is not null && process.CpuPercent >= 2))
        {
            reasons.Add(L["RiskReasonDefender"]);
        }

        return reasons.Count == 0 ? L["RiskReasonStable"] : string.Join(", ", reasons);
    }

    private static string? SecurityProductName(string processName)
    {
        if (processName.Contains("MsMpEng", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("Antimalware", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("NisSrv", StringComparison.OrdinalIgnoreCase))
        {
            return "Microsoft Defender";
        }

        if (processName.Contains("Avast", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("AvastSvc", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("AvastUI", StringComparison.OrdinalIgnoreCase))
        {
            return "Avast";
        }

        if (processName.Contains("AVG", StringComparison.OrdinalIgnoreCase))
        {
            return "AVG";
        }

        if (processName.Contains("Bitdefender", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("bdservicehost", StringComparison.OrdinalIgnoreCase))
        {
            return "Bitdefender";
        }

        if (processName.Contains("ESET", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("ekrn", StringComparison.OrdinalIgnoreCase))
        {
            return "ESET";
        }

        if (processName.Contains("Kaspersky", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("avp", StringComparison.OrdinalIgnoreCase))
        {
            return "Kaspersky";
        }

        if (processName.Contains("Norton", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("nsWscSvc", StringComparison.OrdinalIgnoreCase))
        {
            return "Norton";
        }

        if (processName.Contains("Malwarebytes", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("MBAMService", StringComparison.OrdinalIgnoreCase))
        {
            return "Malwarebytes";
        }

        return null;
    }

    private SessionBaseline CreateBaseline(SystemSnapshot snapshot, ProcessSnapshot starCitizen)
    {
        return new SessionBaseline(
            DateTime.UtcNow,
            snapshot.RamUsedGb,
            snapshot.PagefileUsedGb,
            snapshot.VramUsedGb,
            starCitizen.MemoryMb,
            starCitizen.CommitMb);
    }

    private static SessionBaseline MaxBaseline(SessionBaseline previous, SessionBaseline current)
    {
        return new SessionBaseline(
            current.Time,
            Math.Max(previous.SystemRamGb, current.SystemRamGb),
            Math.Max(previous.PagefileGb, current.PagefileGb),
            Math.Max(previous.VramGb, current.VramGb),
            Math.Max(previous.StarCitizenRamMb, current.StarCitizenRamMb),
            Math.Max(previous.StarCitizenCommitMb, current.StarCitizenCommitMb));
    }

    private string FormatBaselineDelta(SessionBaseline previous, SessionBaseline current)
    {
        return L.Format(
            "StarCitizenEventDelta",
            current.SystemRamGb - previous.SystemRamGb,
            current.PagefileGb - previous.PagefileGb,
            current.VramGb - previous.VramGb,
            current.StarCitizenRamMb - previous.StarCitizenRamMb,
            current.StarCitizenCommitMb - previous.StarCitizenCommitMb);
    }

    private string PressureText(double percent)
    {
        return percent >= 90 ? L["Critical"] : percent >= 75 ? L["Warning"] : L["Stable"];
    }

    private static MediaBrush BrushForPressure(double percent)
    {
        if (percent >= 90)
        {
            return new SolidColorBrush(MediaColor.FromRgb(239, 68, 68));
        }

        if (percent >= 75)
        {
            return new SolidColorBrush(MediaColor.FromRgb(245, 158, 11));
        }

        return new SolidColorBrush(MediaColor.FromRgb(34, 197, 94));
    }

    private static MediaBrush BrushFromRgb(byte r, byte g, byte b)
    {
        return new SolidColorBrush(MediaColor.FromRgb(r, g, b));
    }

    private byte EffectiveDimmingPreviewColor(int value)
    {
        var colorScale = 1 - Math.Clamp(Settings.UiDimmingOpacityPercent, 0, 80) / 100d;
        return (byte)Math.Clamp(Math.Round(Math.Clamp(value, 0, 255) * colorScale), 0, 255);
    }

    private static bool HasValidVram(SystemSnapshot? snapshot)
    {
        return snapshot?.IsGpuDataAvailable == true
            && IsFinite(snapshot.VramUsagePercent)
            && IsFinite(snapshot.VramUsedGb)
            && IsFinite(snapshot.VramTotalGb)
            && snapshot.VramTotalGb > 0;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private sealed record ShaderCacheScanResult(
        IReadOnlyList<ShaderCacheTargetView> Targets,
        long TotalBytes,
        string? LatestBackupPath);

    private sealed record SessionBaseline(
        DateTime Time,
        double SystemRamGb,
        double PagefileGb,
        double VramGb,
        double StarCitizenRamMb,
        double StarCitizenCommitMb);

    private double CalculatePerformanceScore()
    {
        var snapshot = _lastSnapshot;
        if (snapshot is null)
        {
            return 100;
        }

        var pressure = Math.Max(snapshot.CpuUsagePercent, snapshot.RamUsagePercent);
        if (snapshot.IsGpuDataAvailable)
        {
            pressure = Math.Max(pressure, Math.Max(snapshot.GpuUsagePercent, snapshot.VramUsagePercent));
        }

        return Math.Clamp(100 - pressure * 0.45, 0, 100);
    }
}
