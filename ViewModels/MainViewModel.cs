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
    private readonly LocalLearningService _learningService;
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

    public MainViewModel()
    {
        Settings = _settingsService.Load();
        L = new LocalizationService(Settings.Language);
        _protectionService = new ProcessProtectionService(Settings);
        _monitoringService = new MonitoringService(_protectionService);
        _optimizationService = new OptimizationService(Settings, _protectionService, L, AddLog);
        _powerPlanService = new PowerPlanService(L, AddLog);
        _memoryCompressionService = new MemoryCompressionService(L, AddLog);
        _learningService = new LocalLearningService(Settings, L, _settingsService.AppDataDirectory);
        _starCitizenSessionHistoryService = new StarCitizenSessionHistoryService(_settingsService.AppDataDirectory);
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

        Settings.PropertyChanged += SettingsChanged;
        Settings.ProtectionRules.CollectionChanged += ProtectionRulesChanged;
        foreach (var rule in Settings.ProtectionRules)
        {
            rule.PropertyChanged += ProtectionRuleChanged;
        }

        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        RefreshNavigationItems();
        AddLog("Info", L["LogAppStarted"]);
    }

    public event EventHandler? ThemeChanged;
    public event Action<string, string>? NotificationRequested;

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

    public IReadOnlyList<LanguageOption> SupportedLanguages { get; } =
    [
        new("de", "Deutsch"),
        new("en", "English"),
        new("ru", "Russian")
    ];

    public IReadOnlyList<int> HistoryWindows { get; } = [5, 15, 60];
    public IReadOnlyList<AppProfile> Profiles { get; } = Enum.GetValues<AppProfile>();
    public IReadOnlyList<string> Themes { get; } = ["Dark", "Light"];

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
    public string ProcessCountText => Processes.Count.ToString();
    public string LastLogText => Logs.FirstOrDefault()?.Message ?? string.Empty;
    public string TrayText => $"RAM {RamUsagePercent:0}% | CPU {CpuUsagePercent:0}%";
    public string StarCitizenStatusText => _lastSnapshot?.Processes.Any(process => process.IsStarCitizen) == true ? L["StarCitizenDetected"] : L["StarCitizenNotDetected"];
    public string SessionTimeText => _starCitizenSessionStarted is null ? "00:00:00" : (DateTime.UtcNow - _starCitizenSessionStarted.Value).ToString(@"hh\:mm\:ss");
    public string LastSessionTimeText => _lastStarCitizenSessionDuration == TimeSpan.Zero ? "-" : _lastStarCitizenSessionDuration.ToString(@"hh\:mm\:ss");
    public string StarCitizenLogPathText => string.IsNullOrWhiteSpace(Settings.StarCitizenPath)
        ? L["StarCitizenLogAuto"]
        : Settings.StarCitizenPath;
    public string LastStarCitizenExitText => string.IsNullOrWhiteSpace(_lastStarCitizenExitText)
        ? L["StarCitizenExitNone"]
        : _lastStarCitizenExitText;
    public string StarCitizenSessionSummaryText => BuildStarCitizenSessionSummary();
    public string StarCitizenEventDeltaText => BuildStarCitizenEventDelta();
    public string DefenderHintText => BuildDefenderHint();
    public string StarCitizenRiskText => L.Format("SessionRiskValue", CalculateStarCitizenRiskScore(), BuildStarCitizenRiskReason());
    public string LastSessionDisplayText => L.Format("LastSessionValue", LastSessionTimeText);
    public string StarCitizenSessionTotalsText => BuildStarCitizenSessionTotals();
    public string CurrentProfileText => CurrentProfile.ToString();
    public string VramPressureText => _lastSnapshot is null || !_lastSnapshot.IsGpuDataAvailable ? L["Unknown"] : PressureText(_lastSnapshot.VramUsagePercent);
    public string ProtectedStatusText => _lastSnapshot?.Processes.Any(process => process.IsStarCitizen && process.IsProtected) == true ? L["Protected"] : L["NotProtected"];
    public string AutoOptimizationStatusText => Settings.AutoModeEnabled ? L["On"] : L["Off"];
    public string LocalLearningStatusText => Settings.LocalLearningEnabled ? L["On"] : L["Off"];
    public string PrivacyStatusText => Settings.PrivacyMaximumMode ? L["PrivacyMaximum"] : L["PrivacyLocalOnly"];
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
            RefreshNavigationItems();
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

    private void RunMemoryOptimization()
    {
        if (_lastSnapshot is not null)
        {
            _optimizationService.OptimizeMemory(_lastSnapshot.Processes, userRequested: true);
        }
    }

    private void RunCpuOptimization()
    {
        if (_lastSnapshot is not null)
        {
            _optimizationService.OptimizeCpu(_lastSnapshot.Processes, userRequested: true);
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

        if (e.PropertyName is nameof(AppSettings.AutoModeEnabled) or nameof(AppSettings.LocalLearningEnabled) or nameof(AppSettings.PrivacyMaximumMode))
        {
            OnPropertyChanged(nameof(AutoOptimizationStatusText));
            OnPropertyChanged(nameof(LocalLearningStatusText));
            OnPropertyChanged(nameof(PrivacyStatusText));
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
            or nameof(AppSettings.UiDimmingBlue))
        {
            OnPropertyChanged(nameof(UiDimmingOpacityText));
            OnPropertyChanged(nameof(UiDimmingRedText));
            OnPropertyChanged(nameof(UiDimmingGreenText));
            OnPropertyChanged(nameof(UiDimmingBlueText));
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
        if (parameter is string view)
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
            _optimizationService.RunGamingKillSwitch(_lastSnapshot.Processes);
        }

        RunCpuOptimization();
        RunMemoryOptimization();
        AddLog("Info", L["LogStopTasksSafe"]);
    }

    private void ExportLogs()
    {
        var path = Path.Combine(_settingsService.AppDataDirectory, $"logs-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var lines = Logs.Reverse().Select(log => $"{log.Time:O}\t{log.Level}\t{log.Message}");
        File.WriteAllLines(path, lines);
        AddLog("Info", L.Format("LogExported", path));
    }

    public void AddDiagnosticLog(string level, string message)
    {
        AddLog(level, message);
    }

    private void AddLog(string level, string message)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            Logs.Insert(0, new LogEntry { Level = level, Message = message });
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
        Replace(NavigationItems,
        [
            Nav("Dashboard", L["Dashboard"], "⌂"),
            Nav("AI", L["AiRecommendations"], "↯"),
            Nav("Processes", L["ProcessList"], "▦"),
            Nav("Gaming", L["GamingMode"], "🎮"),
            Nav("StarCitizen", L["StarCitizenHub"], "☆"),
            Nav("Hardware", L["HardwareMonitor"], "▣"),
            Nav("Logs", L["Logs"], "≡"),
            Nav("Settings", L["Settings"], "⚙"),
            Nav("Info", L["AppInfo"], "i")
        ]);
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
                AddLog("Info", L.Format("LogStarCitizenPathDetected", directory));
                return;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }
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
    }

    private void LoadStarCitizenSessions()
    {
        var sessions = _starCitizenSessionHistoryService.Load()
            .Select(RefreshSessionLogEvidence)
            .ToList();
        Replace(StarCitizenSessions, sessions);
        SaveStarCitizenSessions();
        OnPropertyChanged(nameof(StarCitizenSessionTotalsText));
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
            return true;
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

    private static DateTime? TryReadLogTimestamp(string line)
    {
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
        var eventView = new StarCitizenEventView
        {
            Type = eventType,
            SessionTime = SessionTimeText,
            Summary = FormatBaselineDelta(previous, current)
        };

        StarCitizenEvents.Insert(0, eventView);
        while (StarCitizenEvents.Count > 12)
        {
            StarCitizenEvents.RemoveAt(StarCitizenEvents.Count - 1);
        }

        _lastMarkedEventBaseline = current;
        AddLog("Info", L.Format("LogStarCitizenEventMarked", eventType, eventView.Summary));
        OnPropertyChanged(nameof(StarCitizenEventDeltaText));
        OnPropertyChanged(nameof(StarCitizenRiskText));
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
