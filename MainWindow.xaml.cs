using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ANEVRED.Models;
using Forms = System.Windows.Forms;
using ANEVRED.Services;
using ANEVRED.ViewModels;
using Drawing = System.Drawing;

namespace ANEVRED;

public partial class MainWindow : Window
{
    private const int WmHotkey = 0x0312;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const int WhKeyboardLl = 13;
    private const int HotkeyServerChange = 101;
    private const int HotkeyRespawn = 102;
    private const int HotkeyStutter = 103;
    private const int HotkeyScreenTranslation = 104;
    private const int HotkeyScreenTranslationCapture = 105;
    private const int HotkeyScreenTranslationRegion = 106;
    private const int HotkeyUiDimming = 107;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkShift = 0x10;

    private readonly MainViewModel _viewModel;
    private readonly Services.ScreenTranslationService _screenTranslationService;
    private readonly DisplayColorFilterService _displayColorFilterService = new();
    private readonly System.Windows.Threading.DispatcherTimer _screenTranslationTimer = new();
    private readonly System.Windows.Threading.DispatcherTimer _uiDimmingAutoTimer = new();
    private readonly LowLevelKeyboardProc _keyboardProc;
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private HwndSource? _source;
    private IntPtr _windowHandle;
    private string? _pendingHotkeyTarget;
    private TranslationOverlayWindow? _translationOverlay;
    private CancellationTokenSource? _screenTranslationCancellation;
    private bool _isScreenTranslationRunning;
    private int _screenTranslationRunId;
    private readonly HashSet<int> _registeredHotkeyIds = [];
    private readonly Dictionary<int, DateTime> _lastHotkeyDispatch = [];
    private IntPtr _keyboardHook;
    private DateTime _lastTranslationToggle = DateTime.MinValue;
    private DateTime _lastTranslationCapture = DateTime.MinValue;
    private WindowState _lastNonMinimizedWindowState = WindowState.Maximized;
    private UiDimmingOverlayWindow? _uiDimmingOverlay;
    private bool _isUiDimmingAutoTuning;
    private const int HotkeyDispatchDebounceMs = 350;

    public MainWindow()
    {
        InitializeComponent();
        _keyboardProc = KeyboardHookCallback;
        _viewModel = new MainViewModel();
        _screenTranslationService = new Services.ScreenTranslationService(
            message => _viewModel.AddDiagnosticLog("Info", "ScreenTranslation: " + message),
            () => Dispatcher.BeginInvoke(new Action(ShowScreenTranslationBusy)));
        DataContext = _viewModel;
        _viewModel.ThemeChanged += (_, _) => ApplyTheme();
        _viewModel.NotificationRequested += ShowNotification;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.TrayText))
            {
                UpdateTrayText();
            }
        };
        _viewModel.L.PropertyChanged += (_, _) =>
        {
            ApplyLocalizedColumnHeaders();
            BuildTrayMenu();
        };
        _viewModel.Settings.PropertyChanged += SettingsPropertyChanged;

        ApplyTheme();
        ApplyLocalizedColumnHeaders();
        LogsGrid.RowHeight = double.NaN;
        BuildTrayMenu();
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            RestoreMainWindowPlacement();
            UpdateUiDimmingAutoTimer();
            ApplyUiDimmingOverlay();
            ApplyDisplayColorFilter();
        };
        StateChanged += (_, _) => RememberMainWindowPlacement();
        LocationChanged += (_, _) => RememberMainWindowPlacement();
        SizeChanged += (_, _) => RememberMainWindowPlacement();
        _screenTranslationTimer.Interval = TimeSpan.FromSeconds(30);
        _screenTranslationTimer.Tick += async (_, _) => await UpdateScreenTranslationAsync();
        _uiDimmingAutoTimer.Interval = TimeSpan.FromSeconds(8);
        _uiDimmingAutoTimer.Tick += (_, _) => AutoTuneUiDimming(measureOriginal: false);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveMainWindowPlacement();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _uiDimmingAutoTimer.Stop();
        _uiDimmingOverlay?.Hide();
        _displayColorFilterService.Restore();
        UnregisterGlobalHotkeys();
        UninstallKeyboardHook();
        _source?.RemoveHook(WndProc);
        _screenTranslationTimer.Stop();
        _screenTranslationService.Dispose();
        _displayColorFilterService.Dispose();
        _translationOverlay?.Close();
        _uiDimmingOverlay?.Close();
        _notifyIcon?.Dispose();
        _trayMenu?.Dispose();
        _viewModel.Settings.PropertyChanged -= SettingsPropertyChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void RestoreMainWindowPlacement()
    {
        var settings = _viewModel.Settings;

        if (settings.MainWindowWidth >= MinWidth)
        {
            Width = settings.MainWindowWidth;
        }

        if (settings.MainWindowHeight >= MinHeight)
        {
            Height = settings.MainWindowHeight;
        }

        if (settings.MainWindowLeft >= 0 && settings.MainWindowTop >= 0)
        {
            Left = settings.MainWindowLeft;
            Top = settings.MainWindowTop;
        }

        WindowState = WindowState.Maximized;

        _lastNonMinimizedWindowState = WindowState == WindowState.Minimized
            ? WindowState.Maximized
            : WindowState;
    }

    private void SaveMainWindowPlacement()
    {
        var settings = _viewModel.Settings;
        var restoreBounds = RestoreBounds;

        var stateToSave = WindowState == WindowState.Minimized
            ? _lastNonMinimizedWindowState
            : WindowState;

        settings.MainWindowState = stateToSave == WindowState.Normal ? "Normal" : "Maximized";

        if (restoreBounds.Width > 0 && restoreBounds.Height > 0)
        {
            settings.MainWindowLeft = Math.Max(0, restoreBounds.Left);
            settings.MainWindowTop = Math.Max(0, restoreBounds.Top);
            settings.MainWindowWidth = restoreBounds.Width;
            settings.MainWindowHeight = restoreBounds.Height;
        }

        _viewModel.PersistSettings();
    }

    private void RememberMainWindowPlacement()
    {
        if (!IsLoaded || WindowState == WindowState.Minimized)
        {
            return;
        }

        _lastNonMinimizedWindowState = WindowState;
        SaveMainWindowPlacement();
    }

    private void ApplyLocalizedColumnHeaders()
    {
        DashboardProcessColumn.Header = _viewModel.L["Process"];
        DashboardPriorityColumn.Header = _viewModel.L["Priority"];
        DashboardAiScoreColumn.Header = _viewModel.L["AiScore"];

        ProcessColumn.Header = _viewModel.L["Process"];
        PidColumn.Header = _viewModel.L["Pid"];
        ProcessCpuColumn.Header = _viewModel.L["Cpu"];
        ProcessGpuColumn.Header = _viewModel.L["Gpu"];
        ProcessRamColumn.Header = _viewModel.L["Ram"];
        ProcessCommitColumn.Header = _viewModel.L["Commit"];
        ProcessVramColumn.Header = _viewModel.L["Vram"];
        PriorityColumn.Header = _viewModel.L["Priority"];
        StatusColumn.Header = _viewModel.L["Status"];
        ProtectedColumn.Header = _viewModel.L["Protected"];
        AiScoreColumn.Header = _viewModel.L["AiScore"];
        WindowColumn.Header = _viewModel.L["WindowTitle"];

        RuleEnabledColumn.Header = _viewModel.L["Enabled"];
        RulePatternColumn.Header = _viewModel.L["Pattern"];
        RuleNotesColumn.Header = _viewModel.L["Notes"];

        LogTimeColumn.Header = _viewModel.L["Time"];
        LogLevelColumn.Header = _viewModel.L["Level"];
        LogMessageColumn.Header = _viewModel.L["Message"];
    }

    private void ApplyTheme()
    {
        if (_viewModel.ThemeMode.Equals("Light", StringComparison.OrdinalIgnoreCase))
        {
            SetBrush("WindowBackgroundBrush", "#F4F7FA");
            SetBrush("SidebarBackgroundBrush", "#D0EAF0F6");
            SetBrush("PanelBackgroundBrush", "#CFFFFFFF");
            SetBrush("PanelAltBackgroundBrush", "#C8EAF0F6");
            SetBrush("PanelHoverBrush", "#D8DCE7F3");
            SetBrush("PrimaryTextBrush", "#10151D");
            SetBrush("SecondaryTextBrush", "#526173");
            SetBrush("DisabledTextBrush", "#7B8794");
            SetBrush("BorderBrushSoft", "#CDD6E0");
            SetBrush("GraphFillBrush", "#B8F8FAFC");
            SetBrush("TableHeaderBrush", "#C8E2EAF4");
            SetBrush("DataGridBackgroundBrush", "#22FFFFFF");
            SetBrush("DataGridRowBackgroundBrush", "#88FFFFFF");
            SetBrush("DataGridAltRowBackgroundBrush", "#80EAF0F6");
            SetBrush("FocusRingBrush", "#2563EB");
            SetBrush("DisabledButtonBackgroundBrush", "#E8EEF5");
            SetBrush("ScrollBarTrackBrush", "#E7EDF4");
            SetBrush("ScrollBarThumbBrush", "#A9B8C8");
            SetBrush("ScrollBarThumbHoverBrush", "#7F93AA");
            SetBrush("CheckBoxBackgroundBrush", "#FFFFFFFF");
            SetBrush("CheckBoxCheckedBackgroundBrush", "#2563EB");
            SetBrush("CheckBoxCheckBrush", "#FFFFFFFF");
            SetBrush("FeatureCardBackgroundBrush", "#F8FBFF");
            SetBrush("FeatureCardHoverBrush", "#EEF6FF");
            SetBrush("FeatureCardCheckedBrush", "#E8F2FF");
            SetBrush("FeatureCardHoverBorderBrush", "#8CBDF7");
            SetBrush("FeatureIconBackgroundBrush", "#EAF4FF");
            SetBrush("FeatureIconInnerBrush", "#FFFFFF");
            SetBrush("FeatureIconBorderBrush", "#9CB8D8");
            SetBrush("FeatureIconCheckedBrush", "#DBEAFF");
            SetBrush("FeatureSwitchBackgroundBrush", "#D8E2EE");
            SetBrush("FeatureSwitchBorderBrush", "#A9B8C8");
            SetBrush("FeatureSwitchCheckedBrush", "#3B82F6");
            SetBrush("FeatureSwitchKnobBrush", "#FFFFFF");
            SetBrush("FeatureSwitchKnobCheckedBrush", "#FFFFFF");
            SetBrush("SliderTrackBrush", "#D8E2EE");
            SetBrush("SliderTrackHoverBrush", "#C8D7E8");
            SetBrush("SliderFillBrush", "#3B82F6");
            SetBrush("SliderThumbBrush", "#FFFFFF");
            SetBrush("SliderThumbBorderBrush", "#2563EB");
            SetBrush("AccentBrush", "#3B82F6");
            SetBrush("RamAccentBrush", "#22C55E");
            SetBrush("CpuAccentBrush", "#F59E0B");
            SetBrush("GpuAccentBrush", "#A855F7");
            SetBrush("VramAccentBrush", "#3B82F6");
            SetBrush("FrametimeAccentBrush", "#F97316");
            SetBrush("CriticalBrush", "#EF4444");
            SetResource("WorkspaceLogoOpacity", 0.075);
            SetResource("LogoContrastEffect", new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 24,
                ShadowDepth = 0,
                Opacity = 0.30
            });
        }
        else
        {
            SetBrush("WindowBackgroundBrush", "#0B111A");
            SetBrush("SidebarBackgroundBrush", "#BC09101A");
            SetBrush("PanelBackgroundBrush", "#A6121A26");
            SetBrush("PanelAltBackgroundBrush", "#B5151F2E");
            SetBrush("PanelHoverBrush", "#C41B2940");
            SetBrush("PrimaryTextBrush", "#F2F6FF");
            SetBrush("SecondaryTextBrush", "#9BA8BC");
            SetBrush("DisabledTextBrush", "#667085");
            SetBrush("BorderBrushSoft", "#263448");
            SetBrush("GraphFillBrush", "#880E1622");
            SetBrush("TableHeaderBrush", "#B8182437");
            SetBrush("DataGridBackgroundBrush", "#220B111A");
            SetBrush("DataGridRowBackgroundBrush", "#66121A26");
            SetBrush("DataGridAltRowBackgroundBrush", "#70151F2E");
            SetBrush("FocusRingBrush", "#60A5FA");
            SetBrush("DisabledButtonBackgroundBrush", "#101722");
            SetBrush("ScrollBarTrackBrush", "#0E1622");
            SetBrush("ScrollBarThumbBrush", "#314157");
            SetBrush("ScrollBarThumbHoverBrush", "#425A78");
            SetBrush("CheckBoxBackgroundBrush", "#111A27");
            SetBrush("CheckBoxCheckedBackgroundBrush", "#1F5FD2");
            SetBrush("CheckBoxCheckBrush", "#FFFFFF");
            SetBrush("FeatureCardBackgroundBrush", "#7C0F1A28");
            SetBrush("FeatureCardHoverBrush", "#A2152438");
            SetBrush("FeatureCardCheckedBrush", "#B70C2036");
            SetBrush("FeatureCardHoverBorderBrush", "#4B6F95");
            SetBrush("FeatureIconBackgroundBrush", "#1418273A");
            SetBrush("FeatureIconInnerBrush", "#15101A26");
            SetBrush("FeatureIconBorderBrush", "#30455F");
            SetBrush("FeatureIconCheckedBrush", "#263B82F6");
            SetBrush("FeatureSwitchBackgroundBrush", "#121A26");
            SetBrush("FeatureSwitchBorderBrush", "#304155");
            SetBrush("FeatureSwitchCheckedBrush", "#263B82F6");
            SetBrush("FeatureSwitchKnobBrush", "#8A98AA");
            SetBrush("FeatureSwitchKnobCheckedBrush", "#EAF6FF");
            SetBrush("SliderTrackBrush", "#263448");
            SetBrush("SliderTrackHoverBrush", "#344A64");
            SetBrush("SliderFillBrush", "#3B82F6");
            SetBrush("SliderThumbBrush", "#EAF6FF");
            SetBrush("SliderThumbBorderBrush", "#60A5FA");
            SetBrush("AccentBrush", "#3B82F6");
            SetBrush("RamAccentBrush", "#22C55E");
            SetBrush("CpuAccentBrush", "#F59E0B");
            SetBrush("GpuAccentBrush", "#A855F7");
            SetBrush("VramAccentBrush", "#3B82F6");
            SetBrush("FrametimeAccentBrush", "#F97316");
            SetBrush("CriticalBrush", "#EF4444");
            SetResource("WorkspaceLogoOpacity", 0.055);
            SetResource("LogoContrastEffect", new DropShadowEffect
            {
                Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#60A5FA"),
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.22
            });
        }
    }

    private static void SetBrush(string key, string color)
    {
        System.Windows.Application.Current.Resources[key] = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }

    private static void SetResource(string key, object value)
    {
        System.Windows.Application.Current.Resources[key] = value;
    }


    private static System.Drawing.Icon LoadApplicationIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/AppIcon.ico", UriKind.Absolute));
            if (resource?.Stream is not null)
            {
                using var icon = new System.Drawing.Icon(resource.Stream);
                return (System.Drawing.Icon)icon.Clone();
            }
        }
        catch
        {
            // Fall back to the default system icon.
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void BuildTrayMenu()
    {
        _trayMenu?.Dispose();
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add(_viewModel.L["Open"], null, (_, _) =>
        {
            Show();
            WindowState = _viewModel.Settings.MainWindowState == "Normal" ? WindowState.Normal : WindowState.Maximized;
            Activate();
        });
        _trayMenu.Items.Add(_viewModel.L["Exit"], null, (_, _) => Close());

        _notifyIcon ??= new Forms.NotifyIcon
        {
            Icon = LoadApplicationIcon(),
            Visible = true
        };

        _notifyIcon.ContextMenuStrip = _trayMenu;
        _notifyIcon.DoubleClick -= NotifyIconDoubleClick;
        _notifyIcon.DoubleClick += NotifyIconDoubleClick;
        UpdateTrayText();
    }

    private void NotifyIconDoubleClick(object? sender, EventArgs e)
    {
        Show();
        WindowState = _viewModel.Settings.MainWindowState == "Normal" ? WindowState.Normal : WindowState.Maximized;
        Activate();
    }

    private void UpdateTrayText()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        var text = "ANEVRED - " + _viewModel.TrayText;
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void ShowNotification(string title, string message)
    {
        _notifyIcon?.ShowBalloonTip(5000, title, message, Forms.ToolTipIcon.Warning);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WndProc);
        RegisterGlobalHotkeys();
        InstallKeyboardHook();
    }

    private void RegisterGlobalHotkeys()
    {
        UnregisterGlobalHotkeys();
        _registeredHotkeyIds.Clear();
        var registered = new List<string>();
        var failed = new List<string>();
        RegisterConfiguredHotkey(HotkeyScreenTranslation, _viewModel.Settings.ScreenTranslationHotkey, registered, failed);
        RegisterConfiguredHotkey(HotkeyScreenTranslationCapture, _viewModel.Settings.ScreenTranslationCaptureHotkey, registered, failed);
        RegisterConfiguredHotkey(HotkeyScreenTranslationRegion, _viewModel.Settings.ScreenTranslationRegionHotkey, registered, failed);
        RegisterConfiguredHotkey(HotkeyUiDimming, _viewModel.Settings.UiDimmingHotkey, registered, failed);

        if (!_viewModel.Settings.StarCitizenHotkeysEnabled)
        {
            _viewModel.SetHotkeyStatus(failed.Count == 0, _viewModel.L["StarCitizenHotkeysOff"]);
            return;
        }

        RegisterConfiguredHotkey(HotkeyServerChange, _viewModel.Settings.StarCitizenServerChangeHotkey, registered, failed);
        RegisterConfiguredHotkey(HotkeyRespawn, _viewModel.Settings.StarCitizenRespawnHotkey, registered, failed);
        RegisterConfiguredHotkey(HotkeyStutter, _viewModel.Settings.StarCitizenStutterHotkey, registered, failed);
        _viewModel.SetHotkeyStatus(failed.Count == 0, failed.Count == 0 ? string.Join(", ", registered) : string.Join(", ", failed));
    }

    private void UnregisterGlobalHotkeys()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        UnregisterHotKey(_windowHandle, HotkeyServerChange);
        UnregisterHotKey(_windowHandle, HotkeyRespawn);
        UnregisterHotKey(_windowHandle, HotkeyStutter);
        UnregisterHotKey(_windowHandle, HotkeyScreenTranslation);
        UnregisterHotKey(_windowHandle, HotkeyScreenTranslationCapture);
        UnregisterHotKey(_windowHandle, HotkeyScreenTranslationRegion);
        UnregisterHotKey(_windowHandle, HotkeyUiDimming);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
        {
            return IntPtr.Zero;
        }

        handled = true;
        DispatchHotkey(wParam.ToInt32());

        return IntPtr.Zero;
    }

    private void DispatchHotkey(int hotkeyId)
    {
        var now = DateTime.UtcNow;
        if (_lastHotkeyDispatch.TryGetValue(hotkeyId, out var previous)
            && (now - previous).TotalMilliseconds < HotkeyDispatchDebounceMs)
        {
            return;
        }

        _lastHotkeyDispatch[hotkeyId] = now;
        switch (hotkeyId)
        {
            case HotkeyServerChange:
                _viewModel.MarkServerChangeCommand.Execute(null);
                break;
            case HotkeyRespawn:
                _viewModel.MarkRespawnCommand.Execute(null);
                break;
            case HotkeyStutter:
                _viewModel.MarkStutterCommand.Execute(null);
                break;
            case HotkeyScreenTranslation:
                TryToggleScreenTranslation();
                break;
            case HotkeyScreenTranslationCapture:
                TryCaptureScreenTranslation();
                break;
            case HotkeyScreenTranslationRegion:
                TryChooseTranslationRegion();
                break;
            case HotkeyUiDimming:
                ToggleUiDimming();
                break;
        }
    }

    private void RegisterConfiguredHotkey(int id, string hotkeyText, List<string> registered, List<string> failed)
    {
        if (!TryParseHotkey(hotkeyText, out var modifiers, out var key))
        {
            failed.Add(hotkeyText);
            return;
        }

        if (RegisterHotKey(_windowHandle, id, modifiers | ModNoRepeat, key))
        {
            _registeredHotkeyIds.Add(id);
            registered.Add(hotkeyText);
        }
        else
        {
            failed.Add(hotkeyText);
        }
    }

    private static bool TryParseHotkey(string hotkeyText, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;

        var parts = hotkeyText
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        if (parts.Length == 0)
        {
            return false;
        }

        foreach (var part in parts)
        {
            var normalized = part.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);
            if (normalized.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Control", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Strg", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModControl;
                continue;
            }

            if (normalized.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModAlt;
                continue;
            }

            if (normalized.Equals("Shift", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Umschalt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= 0x0004;
                continue;
            }

            if (key != 0)
            {
                return false;
            }

            key = ParseVirtualKey(normalized);
        }

        return key != 0;
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

        return 0;
    }

    private void SettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.StarCitizenHotkeysEnabled)
            or nameof(AppSettings.StarCitizenServerChangeHotkey)
            or nameof(AppSettings.StarCitizenRespawnHotkey)
            or nameof(AppSettings.StarCitizenStutterHotkey)
            or nameof(AppSettings.ScreenTranslationHotkey)
            or nameof(AppSettings.ScreenTranslationCaptureHotkey)
            or nameof(AppSettings.ScreenTranslationRegionHotkey)
            or nameof(AppSettings.UiDimmingHotkey))
        {
            RegisterGlobalHotkeys();
        }

        if (e.PropertyName is nameof(AppSettings.ScreenTranslationEnabled))
        {
            if (_viewModel.Settings.ScreenTranslationEnabled)
            {
                StartScreenTranslation();
            }
            else
            {
                StopScreenTranslation();
            }
        }

        if (e.PropertyName == nameof(AppSettings.ScreenTranslationAutoRefresh))
        {
            if (_viewModel.Settings.ScreenTranslationEnabled && _viewModel.Settings.ScreenTranslationAutoRefresh)
            {
                _screenTranslationTimer.Start();
            }
            else
            {
                _screenTranslationTimer.Stop();
            }
        }

        if (e.PropertyName is nameof(AppSettings.UiDimmingEnabled)
            or nameof(AppSettings.UiDimmingAutoTuneEnabled)
            or nameof(AppSettings.UiDimmingOpacityPercent)
            or nameof(AppSettings.UiDimmingRed)
            or nameof(AppSettings.UiDimmingGreen)
            or nameof(AppSettings.UiDimmingBlue))
        {
            UpdateUiDimmingAutoTimer();
            ApplyUiDimmingOverlay();
        }

        if (e.PropertyName is nameof(AppSettings.UiColorFilterEnabled)
            or nameof(AppSettings.UiColorFilterRedPercent)
            or nameof(AppSettings.UiColorFilterGreenPercent)
            or nameof(AppSettings.UiColorFilterBluePercent)
            or nameof(AppSettings.UiColorFilterContrastPercent)
            or nameof(AppSettings.UiColorFilterBrightnessPercent)
            or nameof(AppSettings.UiColorFilterGammaPercent)
            or nameof(AppSettings.UiColorFilterTemperature)
            or nameof(AppSettings.UiColorFilterTint))
        {
            ApplyDisplayColorFilter();
        }

    }

    private void ApplyDisplayColorFilter()
    {
        var settings = _viewModel.Settings;
        if (!settings.UiColorFilterEnabled)
        {
            _displayColorFilterService.Restore();
            return;
        }

        var applied = _displayColorFilterService.Apply(
            settings.UiColorFilterRedPercent,
            settings.UiColorFilterGreenPercent,
            settings.UiColorFilterBluePercent,
            settings.UiColorFilterContrastPercent,
            settings.UiColorFilterBrightnessPercent,
            settings.UiColorFilterGammaPercent,
            settings.UiColorFilterTemperature,
            settings.UiColorFilterTint,
            ResolveUiDimmingTargetBounds());
        if (!applied)
        {
            _viewModel.AddDiagnosticLog("Warn", "Display color filter could not be applied.");
        }
    }

    private void ApplyColorFilterPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string preset })
        {
            return;
        }

        var settings = _viewModel.Settings;
        settings.UiColorFilterEnabled = true;

        switch (preset)
        {
            case "Film":
                settings.UiColorFilterRedPercent = 104;
                settings.UiColorFilterGreenPercent = 100;
                settings.UiColorFilterBluePercent = 96;
                settings.UiColorFilterContrastPercent = 104;
                settings.UiColorFilterBrightnessPercent = 2;
                settings.UiColorFilterGammaPercent = 103;
                settings.UiColorFilterTemperature = 12;
                settings.UiColorFilterTint = 2;
                break;
            case "EyeProtect":
                settings.UiColorFilterRedPercent = 100;
                settings.UiColorFilterGreenPercent = 92;
                settings.UiColorFilterBluePercent = 82;
                settings.UiColorFilterContrastPercent = 98;
                settings.UiColorFilterBrightnessPercent = -2;
                settings.UiColorFilterGammaPercent = 98;
                settings.UiColorFilterTemperature = 18;
                settings.UiColorFilterTint = -4;
                break;
            default:
                settings.UiColorFilterRedPercent = 100;
                settings.UiColorFilterGreenPercent = 96;
                settings.UiColorFilterBluePercent = 102;
                settings.UiColorFilterContrastPercent = 106;
                settings.UiColorFilterBrightnessPercent = 3;
                settings.UiColorFilterGammaPercent = 106;
                settings.UiColorFilterTemperature = -4;
                settings.UiColorFilterTint = 0;
                break;
        }

        ApplyDisplayColorFilter();
    }

    private void ResetColorFilterClick(object sender, RoutedEventArgs e)
    {
        ResetColorFilterSettings();
        ApplyDisplayColorFilter();
    }

    private void ResetColorFilterSettings()
    {
        var settings = _viewModel.Settings;
        settings.UiColorFilterEnabled = false;
        settings.UiColorFilterRedPercent = 100;
        settings.UiColorFilterGreenPercent = 100;
        settings.UiColorFilterBluePercent = 100;
        settings.UiColorFilterContrastPercent = 100;
        settings.UiColorFilterBrightnessPercent = 0;
        settings.UiColorFilterGammaPercent = 100;
        settings.UiColorFilterTemperature = 0;
        settings.UiColorFilterTint = 0;
    }

    private void ApplyUiDimmingOverlay()
    {
        var settings = _viewModel.Settings;
        if (!settings.UiDimmingEnabled || settings.UiDimmingOpacityPercent <= 0)
        {
            _uiDimmingOverlay?.Hide();
            return;
        }

        _uiDimmingOverlay ??= new UiDimmingOverlayWindow();
        _uiDimmingOverlay.Apply(
            settings.UiDimmingRed,
            settings.UiDimmingGreen,
            settings.UiDimmingBlue,
            settings.UiDimmingOpacityPercent,
            ResolveUiDimmingTargetBounds());

        if (!_uiDimmingOverlay.IsVisible)
        {
            _uiDimmingOverlay.Show();
        }

        _uiDimmingOverlay.Topmost = false;
        _uiDimmingOverlay.Topmost = true;
    }

    private void UpdateUiDimmingAutoTimer()
    {
        if (_viewModel.Settings.UiDimmingEnabled && _viewModel.Settings.UiDimmingAutoTuneEnabled)
        {
            if (!_uiDimmingAutoTimer.IsEnabled)
            {
                _uiDimmingAutoTimer.Start();
            }

            AutoTuneUiDimming(measureOriginal: false);
        }
        else
        {
            _uiDimmingAutoTimer.Stop();
        }
    }

    private void AnalyzeUiDimmingClick(object sender, RoutedEventArgs e)
    {
        _viewModel.Settings.UiDimmingEnabled = true;
        AutoTuneUiDimming(measureOriginal: true);
        ApplyUiDimmingOverlay();
    }

    private void ToggleUiDimming()
    {
        _viewModel.Settings.UiDimmingEnabled = !_viewModel.Settings.UiDimmingEnabled;
        _viewModel.AddDiagnosticLog("Info", $"UI dimming toggled: {(_viewModel.Settings.UiDimmingEnabled ? "on" : "off")}.");
    }

    private void AutoTuneUiDimming(bool measureOriginal)
    {
        if (_isUiDimmingAutoTuning || !_viewModel.Settings.UiDimmingEnabled)
        {
            return;
        }

        _isUiDimmingAutoTuning = true;
        var wasVisible = _uiDimmingOverlay?.IsVisible == true;
        try
        {
            var targetBounds = ResolveUiDimmingTargetBounds();
            if (targetBounds.Width <= 0 || targetBounds.Height <= 0)
            {
                return;
            }

            if (measureOriginal && wasVisible)
            {
                _uiDimmingOverlay!.Hide();
                Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                Thread.Sleep(80);
            }

            var suggestion = AnalyzeDimmingSuggestion(targetBounds, compensateCurrentOverlay: !measureOriginal && wasVisible);
            if (!measureOriginal)
            {
                suggestion = SmoothDimmingSuggestion(suggestion);
            }

            ApplyDimmingSuggestion(suggestion);
            ApplyUiDimmingOverlay();
            _viewModel.AddDiagnosticLog("Info", $"UI dimming tuned: RGB {suggestion.Red}/{suggestion.Green}/{suggestion.Blue}, {suggestion.OpacityPercent:0}% on {targetBounds.Width}x{targetBounds.Height}.");
        }
        catch (Exception ex)
        {
            _viewModel.AddDiagnosticLog("Warn", "UI dimming auto tune failed: " + ex.Message);
        }
        finally
        {
            _isUiDimmingAutoTuning = false;
            if (measureOriginal && wasVisible)
            {
                ApplyUiDimmingOverlay();
            }
        }
    }

    private void ApplyDimmingSuggestion(DimmingSuggestion suggestion)
    {
        var settings = _viewModel.Settings;
        settings.UiDimmingRed = suggestion.Red;
        settings.UiDimmingGreen = suggestion.Green;
        settings.UiDimmingBlue = suggestion.Blue;
        settings.UiDimmingOpacityPercent = suggestion.OpacityPercent;
    }

    private DimmingSuggestion SmoothDimmingSuggestion(DimmingSuggestion suggestion)
    {
        var settings = _viewModel.Settings;
        var red = SmoothColor(settings.UiDimmingRed, suggestion.Red);
        var green = SmoothColor(settings.UiDimmingGreen, suggestion.Green);
        var blue = SmoothColor(settings.UiDimmingBlue, suggestion.Blue);
        var opacity = SmoothValue(settings.UiDimmingOpacityPercent, suggestion.OpacityPercent);

        return new DimmingSuggestion(red, green, blue, opacity);
    }

    private DimmingSuggestion AnalyzeDimmingSuggestion(Drawing.Rectangle bounds, bool compensateCurrentOverlay)
    {
        using var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        }

        var stepX = Math.Max(1, bitmap.Width / 96);
        var stepY = Math.Max(1, bitmap.Height / 54);
        double red = 0;
        double green = 0;
        double blue = 0;
        double luma = 0;
        var brightPixels = 0;
        var count = 0;

        for (var y = 0; y < bitmap.Height; y += stepY)
        {
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                var pixel = bitmap.GetPixel(x, y);
                var sample = compensateCurrentOverlay
                    ? EstimateOriginalPixel(pixel)
                    : (Red: (double)pixel.R, Green: (double)pixel.G, Blue: (double)pixel.B);
                var pixelLuma = 0.2126 * sample.Red + 0.7152 * sample.Green + 0.0722 * sample.Blue;
                red += sample.Red;
                green += sample.Green;
                blue += sample.Blue;
                luma += pixelLuma;
                if (pixelLuma >= 205)
                {
                    brightPixels++;
                }

                count++;
            }
        }

        if (count == 0)
        {
            return new DimmingSuggestion(0, 0, 0, 30);
        }

        red /= count;
        green /= count;
        blue /= count;
        luma /= count;
        var brightRatio = (double)brightPixels / count;
        var brightnessFactor = Math.Clamp((luma - 65) / 155, 0, 1);
        var glareFactor = Math.Sqrt(Math.Clamp(brightRatio, 0, 1));
        var opacity = Math.Clamp(14 + brightnessFactor * 27 + glareFactor * 26, 14, 65);
        var counterRed = ClampColor(6 + (255 - red) * 0.10);
        var counterGreen = ClampColor(8 + (255 - green) * 0.12);
        var counterBlue = ClampColor(14 + (255 - blue) * 0.18);

        if (red + green > blue * 2.15)
        {
            counterBlue = Math.Max(counterBlue, 42);
            counterGreen = Math.Min(counterGreen, 24);
            counterRed = Math.Min(counterRed, 18);
        }

        return new DimmingSuggestion(counterRed, counterGreen, counterBlue, opacity);
    }

    private (double Red, double Green, double Blue) EstimateOriginalPixel(Drawing.Color pixel)
    {
        var settings = _viewModel.Settings;
        var alpha = Math.Clamp(settings.UiDimmingOpacityPercent / 100d, 0, 0.80);
        if (alpha <= 0.01)
        {
            return (pixel.R, pixel.G, pixel.B);
        }

        var sourceWeight = Math.Max(0.20, 1 - alpha);
        var overlayRed = EffectiveDimmingColor(settings.UiDimmingRed, settings.UiDimmingOpacityPercent);
        var overlayGreen = EffectiveDimmingColor(settings.UiDimmingGreen, settings.UiDimmingOpacityPercent);
        var overlayBlue = EffectiveDimmingColor(settings.UiDimmingBlue, settings.UiDimmingOpacityPercent);
        return (
            Math.Clamp((pixel.R - overlayRed * alpha) / sourceWeight, 0, 255),
            Math.Clamp((pixel.G - overlayGreen * alpha) / sourceWeight, 0, 255),
            Math.Clamp((pixel.B - overlayBlue * alpha) / sourceWeight, 0, 255));
    }

    private Drawing.Rectangle ResolveUiDimmingTargetBounds()
    {
        var starCitizenHandle = FindStarCitizenWindowHandle();
        if (starCitizenHandle != IntPtr.Zero)
        {
            return Forms.Screen.FromHandle(starCitizenHandle).Bounds;
        }

        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            return Forms.Screen.FromHandle(foreground).Bounds;
        }

        return Forms.Screen.PrimaryScreen?.Bounds ?? Forms.SystemInformation.VirtualScreen;
    }

    private static IntPtr FindStarCitizenWindowHandle()
    {
        foreach (var process in Process.GetProcessesByName("StarCitizen"))
        {
            using (process)
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    return process.MainWindowHandle;
                }
            }
        }

        return IntPtr.Zero;
    }

    private static int ClampColor(double value) => (int)Math.Clamp(Math.Round(value), 0, 255);

    private static double EffectiveDimmingColor(int value, double opacityPercent)
    {
        var colorScale = 1 - Math.Clamp(opacityPercent, 0, 80) / 100d;
        return Math.Clamp(value, 0, 255) * colorScale;
    }

    private static int SmoothColor(int current, int target)
    {
        var delta = target - current;
        if (Math.Abs(delta) < 4)
        {
            return current;
        }

        return (int)Math.Clamp(Math.Round(current + delta * 0.25), 0, 255);
    }

    private static double SmoothValue(double current, double target)
    {
        var delta = target - current;
        if (Math.Abs(delta) < 1.5)
        {
            return current;
        }

        return Math.Clamp(current + delta * 0.25, 0, 80);
    }

    private readonly record struct DimmingSuggestion(int Red, int Green, int Blue, double OpacityPercent);

    private void CaptureHotkeyButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string target)
        {
            return;
        }

        _pendingHotkeyTarget = target;
        UnregisterGlobalHotkeys();
        _viewModel.SetHotkeyStatus(true, _viewModel.L["HotkeyCapturePrompt"]);
        Focus();
        Keyboard.Focus(this);
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (_pendingHotkeyTarget is null)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _pendingHotkeyTarget = null;
            RegisterGlobalHotkeys();
            _viewModel.SetHotkeyStatus(true, _viewModel.L["HotkeyCaptureCanceled"]);
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin)
        {
            return;
        }

        var hotkey = BuildHotkeyText(Keyboard.Modifiers, key);
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            _viewModel.SetHotkeyStatus(false, _viewModel.L["HotkeyCaptureInvalid"]);
            return;
        }

        switch (_pendingHotkeyTarget)
        {
            case "ServerChange":
                _viewModel.Settings.StarCitizenServerChangeHotkey = hotkey;
                break;
            case "Respawn":
                _viewModel.Settings.StarCitizenRespawnHotkey = hotkey;
                break;
            case "Stutter":
                _viewModel.Settings.StarCitizenStutterHotkey = hotkey;
                break;
            case "Translation":
                _viewModel.Settings.ScreenTranslationHotkey = hotkey;
                break;
            case "TranslationCapture":
                _viewModel.Settings.ScreenTranslationCaptureHotkey = hotkey;
                break;
            case "TranslationRegion":
                _viewModel.Settings.ScreenTranslationRegionHotkey = hotkey;
                break;
            case "UiDimming":
                _viewModel.Settings.UiDimmingHotkey = hotkey;
                break;
        }

        _pendingHotkeyTarget = null;
        RegisterGlobalHotkeys();
    }

    private void BrowseStarCitizenPathClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = _viewModel.L["StarCitizenPath"],
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            _viewModel.SetStarCitizenPath(dialog.SelectedPath);
        }
    }

    private void ToggleScreenTranslation()
    {
        _viewModel.Settings.ScreenTranslationEnabled = !_viewModel.Settings.ScreenTranslationEnabled;
    }

    private void TryToggleScreenTranslation()
    {
        if ((DateTime.UtcNow - _lastTranslationToggle) < TimeSpan.FromMilliseconds(500))
        {
            _viewModel.AddDiagnosticLog("Info", "ScreenTranslation: toggle ignored by debounce.");
            return;
        }

        _lastTranslationToggle = DateTime.UtcNow;
        _viewModel.AddDiagnosticLog("Info", "ScreenTranslation: toggle requested.");
        ToggleScreenTranslation();
    }

    private void TryCaptureScreenTranslation()
    {
        if ((DateTime.UtcNow - _lastTranslationCapture) < TimeSpan.FromMilliseconds(500))
        {
            _viewModel.AddDiagnosticLog("Info", "ScreenTranslation: capture ignored by debounce.");
            return;
        }

        if (_isScreenTranslationRunning)
        {
            _viewModel.AddDiagnosticLog("Info", "ScreenTranslation: capture ignored because translation is still running.");
            return;
        }

        _lastTranslationCapture = DateTime.UtcNow;
        _viewModel.AddDiagnosticLog("Info", "ScreenTranslation: capture requested.");
        if (!_viewModel.Settings.ScreenTranslationEnabled)
        {
            _viewModel.Settings.ScreenTranslationEnabled = true;
        }

        _ = UpdateScreenTranslationAsync();
    }

    private void ShowScreenTranslationBusy()
    {
        if (_translationOverlay is null || !_viewModel.Settings.ScreenTranslationEnabled)
        {
            return;
        }

        _translationOverlay.SetCaptureMode(false);
        _translationOverlay.SetText("Übersetzung läuft...", string.Empty);
        if (!_translationOverlay.IsVisible)
        {
            _translationOverlay.Show();
        }

        _translationOverlay.Topmost = true;
        _viewModel.AddDiagnosticLog("Info", "ScreenTranslation: OCR done, waiting for Chrome translator.");
    }

    private void TryChooseTranslationRegion()
    {
        _viewModel.AddDiagnosticLog("Info", "ScreenTranslation: region selection requested.");
        ChooseTranslationRegion(restoreMainWindow: false);
    }

    private void StartScreenTranslation()
    {
        _screenTranslationCancellation?.Cancel();
        _screenTranslationCancellation?.Dispose();
        _screenTranslationCancellation = new CancellationTokenSource();
        var runId = ++_screenTranslationRunId;
        _translationOverlay ??= new TranslationOverlayWindow();
        _translationOverlay.SetRegion(CurrentTranslationRegion());
        _translationOverlay.SetText(string.Empty, "Live-Übersetzung aktiv.");
        _translationOverlay.Show();
        _translationOverlay.Topmost = true;
        if (_viewModel.Settings.ScreenTranslationAutoRefresh)
        {
            _screenTranslationTimer.Start();
        }
        _viewModel.AddDiagnosticLog("Info", $"ScreenTranslation: overlay start visible={_translationOverlay.IsVisible}, enabled={_viewModel.Settings.ScreenTranslationEnabled}, region={CurrentTranslationRegion()}.");
        _ = WarmUpThenUpdateScreenTranslationAsync(runId, _screenTranslationCancellation.Token);
    }

    private async Task WarmUpThenUpdateScreenTranslationAsync(int runId, CancellationToken cancellationToken)
    {
        if (_viewModel.Settings.ScreenTranslationAutoRefresh && IsCurrentScreenTranslationRun(runId, cancellationToken))
        {
            await UpdateScreenTranslationAsync(cancellationToken, runId);
        }
    }

    private void StopScreenTranslation()
    {
        _viewModel.AddDiagnosticLog("Info", $"ScreenTranslation: overlay stop visible={_translationOverlay?.IsVisible}, enabled={_viewModel.Settings.ScreenTranslationEnabled}.");
        ++_screenTranslationRunId;
        _screenTranslationTimer.Stop();
        _translationOverlay?.Hide();
        _screenTranslationCancellation?.Cancel();
        _screenTranslationCancellation?.Dispose();
        _screenTranslationCancellation = null;
        _screenTranslationService.CancelActiveTranslation();
        _isScreenTranslationRunning = false;
    }

    private async Task UpdateScreenTranslationAsync(CancellationToken cancellationToken = default, int? runId = null)
    {
        if (!cancellationToken.CanBeCanceled && _screenTranslationCancellation is not null)
        {
            cancellationToken = _screenTranslationCancellation.Token;
        }

        var activeRunId = runId ?? _screenTranslationRunId;
        if (_isScreenTranslationRunning || !IsCurrentScreenTranslationRun(activeRunId, cancellationToken))
        {
            return;
        }

        _isScreenTranslationRunning = true;
        try
        {
            var region = CurrentTranslationRegion();
            if (!IsCurrentScreenTranslationRun(activeRunId, cancellationToken))
            {
                return;
            }

            _translationOverlay ??= new TranslationOverlayWindow();
            _translationOverlay.SetRegion(region);
            _viewModel.AddDiagnosticLog("Info", $"ScreenTranslation: update begin visible={_translationOverlay.IsVisible}, enabled={_viewModel.Settings.ScreenTranslationEnabled}, region={region}.");
            var captureRegion = ToCaptureRectangle(region);
            if (captureRegion.Width < 20 || captureRegion.Height < 20)
            {
                _translationOverlay.SetText(string.Empty, "Übersetzungsbereich liegt außerhalb des sichtbaren Bildschirms.");
                _viewModel.AddDiagnosticLog("Warn", $"ScreenTranslation: capture region invalid after screen clamp: {captureRegion}.");
                return;
            }

            var wasVisible = _translationOverlay.IsVisible;
            ScreenTranslationResult result;
            _viewModel.AddDiagnosticLog("Info", $"ScreenTranslation: capture with selected overlay text paused, visible={wasVisible}.");

            try
            {
                // Hide the overlay completely during capture. Opacity=0 is not enough on some
                // systems because the transparent/layered WPF window can still end up in the
                // captured frame and Windows OCR then sees an empty/dimmed rectangle.
                _translationOverlay.SetCaptureMode(true);
                if (wasVisible)
                {
                    _translationOverlay.Hide();
                }

                await Task.Delay(350, cancellationToken);
                if (!IsCurrentScreenTranslationRun(activeRunId, cancellationToken))
                {
                    return;
                }

                result = await _screenTranslationService.TranslateRegionAsync(
                    captureRegion,
                    _viewModel.Settings.ScreenTranslationTargetLanguage,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _viewModel.AddDiagnosticLog("Info", "ScreenTranslation: update cancelled.");
                return;
            }
            catch (Exception ex)
            {
                result = new ScreenTranslationResult
                {
                    Status = "OCR fehlgeschlagen: " + ex.Message,
                    TranslatedText = string.Empty,
                    IsAvailable = false
                };
            }
            finally
            {
                if (wasVisible && IsCurrentScreenTranslationRun(activeRunId, cancellationToken))
                {
                    if (!_translationOverlay.IsVisible)
                    {
                        _translationOverlay.Show();
                        _viewModel.AddDiagnosticLog("Info", "ScreenTranslation: overlay was hidden, show called.");
                    }

                    _translationOverlay.SetRegion(region);
                    _translationOverlay.SetCaptureMode(false);
                    // Do not toggle Topmost here. On one-monitor setups this steals focus and can
                    // bring the overlay/tool window into the selected capture area.
                    _viewModel.AddDiagnosticLog("Info", $"ScreenTranslation: overlay refreshed visible={_translationOverlay.IsVisible}.");
                }
            }

            if (!IsCurrentScreenTranslationRun(activeRunId, cancellationToken))
            {
                return;
            }

            _translationOverlay.SetStructuredText(result.Segments, result.TranslatedText, result.Status);
            _viewModel.AddDiagnosticLog("Info", $"ScreenTranslation: update end visible={_translationOverlay.IsVisible}, textChars={result.TranslatedText.Length}, segments={result.Segments.Count}, status={result.Status}");
        }
        catch (Exception ex)
        {
            _viewModel.AddDiagnosticLog("Warn", "ScreenTranslation: update failed: " + ex.Message);
        }
        finally
        {
            _isScreenTranslationRunning = false;
        }
    }

    private bool IsCurrentScreenTranslationRun(int runId, CancellationToken cancellationToken)
    {
        return _viewModel.Settings.ScreenTranslationEnabled
            && !cancellationToken.IsCancellationRequested
            && runId == _screenTranslationRunId;
    }

    private Rect CurrentTranslationRegion()
    {
        return new Rect(
            _viewModel.Settings.ScreenTranslationLeft,
            _viewModel.Settings.ScreenTranslationTop,
            _viewModel.Settings.ScreenTranslationWidth,
            _viewModel.Settings.ScreenTranslationHeight);
    }

    private static System.Drawing.Rectangle ToCaptureRectangle(Rect region)
    {
        var screen = Forms.SystemInformation.VirtualScreen;
        var left = Math.Max(screen.Left, (int)Math.Round(region.Left));
        var top = Math.Max(screen.Top, (int)Math.Round(region.Top));
        var right = Math.Min(screen.Right, (int)Math.Round(region.Right));
        var bottom = Math.Min(screen.Bottom, (int)Math.Round(region.Bottom));
        return new System.Drawing.Rectangle(
            left,
            top,
            Math.Max(0, right - left),
            Math.Max(0, bottom - top));
    }

    private void ChooseTranslationRegionClick(object sender, RoutedEventArgs e)
    {
        ChooseTranslationRegion(restoreMainWindow: true);
    }

    private void CaptureScreenTranslationClick(object sender, RoutedEventArgs e)
    {
        TryCaptureScreenTranslation();
    }

    private async void CheckChromeTranslationClick(object sender, RoutedEventArgs e)
    {
        _viewModel.AddDiagnosticLog("Info", "ScreenTranslation: Chrome self-check requested.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var ok = await _screenTranslationService.CheckChromeTranslationAsync(
                _viewModel.Settings.ScreenTranslationTargetLanguage,
                timeout.Token);
            _viewModel.AddDiagnosticLog(
                ok ? "Info" : "Warn",
                ok
                    ? "ScreenTranslation: Chrome self-check passed."
                    : "ScreenTranslation: Chrome self-check did not produce a usable translation.");
        }
        catch (OperationCanceledException)
        {
            _viewModel.AddDiagnosticLog("Warn", "ScreenTranslation: Chrome self-check timed out.");
        }
        catch (Exception ex)
        {
            _viewModel.AddDiagnosticLog("Warn", "ScreenTranslation: Chrome self-check failed: " + ex.Message);
        }
    }

    private void ChooseTranslationRegion(bool restoreMainWindow)
    {
        var wasEnabled = _viewModel.Settings.ScreenTranslationEnabled;
        var mainWasVisible = IsVisible;
        var previousWindowState = WindowState;
        var previousForeground = GetForegroundWindow();
        _viewModel.AddDiagnosticLog("Info", $"ScreenTranslation: choose region start, wasEnabled={wasEnabled}.");
        StopScreenTranslation();

        // On a single monitor the tool window itself can cover the area the user wants to select.
        // Minimize it while the selection overlay is active and do not set it as Owner, otherwise
        // WPF keeps bringing the owner/tool window back to the foreground.
        if (mainWasVisible)
        {
            WindowState = WindowState.Minimized;
        }

        try
        {
            var selector = new RegionSelectionWindow();
            var selectedRegion = restoreMainWindow
                ? selector.ShowDialog() == true ? selector.SelectedRegion : null
                : ShowRegionSelectorWithoutAppReactivation(selector);

            if (selectedRegion is { } region)
            {
                _viewModel.AddDiagnosticLog("Info", $"ScreenTranslation: region selected {region}.");
                _viewModel.Settings.ScreenTranslationLeft = region.Left;
                _viewModel.Settings.ScreenTranslationTop = region.Top;
                _viewModel.Settings.ScreenTranslationWidth = region.Width;
                _viewModel.Settings.ScreenTranslationHeight = region.Height;
            }
        }
        finally
        {
            if (mainWasVisible && restoreMainWindow)
            {
                Show();
                WindowState = previousWindowState;
            }
            else if (!restoreMainWindow)
            {
                if (mainWasVisible)
                {
                    Show();
                    WindowState = WindowState.Minimized;
                }

                RestorePreviousForeground(previousForeground);
            }
        }

        if (wasEnabled)
        {
            StartScreenTranslation();
        }

        _viewModel.AddDiagnosticLog("Info", "ScreenTranslation: choose region end.");
    }

    private static Rect? ShowRegionSelectorWithoutAppReactivation(RegionSelectionWindow selector)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        selector.Closed += (_, _) => frame.Continue = false;
        selector.Show();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        return selector.SelectedRegion;
    }

    private void RestorePreviousForeground(IntPtr previousForeground)
    {
        if (previousForeground == IntPtr.Zero || previousForeground == _windowHandle)
        {
            return;
        }

        _ = RestorePreviousForegroundAsync(previousForeground);
    }

    private async Task RestorePreviousForegroundAsync(IntPtr previousForeground)
    {
        // WPF/Windows can perform activation after the modal/selection window closes.
        // Retry shortly after close so the selected game/window wins foreground again.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (GetForegroundWindow() == previousForeground)
            {
                return;
            }

            _ = SetForegroundWindow(previousForeground);
            await Task.Delay(75);
        }
    }

    private static string BuildHotkeyText(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (parts.Count == 0)
        {
            return string.Empty;
        }

        var keyText = FormatHotkeyKey(key);
        if (string.IsNullOrWhiteSpace(keyText))
        {
            return string.Empty;
        }

        parts.Add(keyText);
        return string.Join("+", parts);
    }

    private static string FormatHotkeyKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return key.ToString();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)key - (int)Key.D0).ToString();
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            return key.ToString();
        }

        return string.Empty;
    }

    private void ProcessGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid dataGrid)
        {
            return;
        }

        var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null)
        {
            return;
        }

        row.IsSelected = true;
        dataGrid.SelectedItem = row.Item;
        dataGrid.Focus();
    }

    private static T? FindParent<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            return;
        }

        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, IntPtr.Zero, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            _viewModel.AddDiagnosticLog("Warn", $"Low-level keyboard hook could not be installed. Win32 {error}. Run ANEVRED as administrator if the game runs elevated.");
        }
        else
        {
            _viewModel.AddDiagnosticLog("Info", "Low-level keyboard hook installed for in-game hotkeys.");
        }
    }

    private void UninstallKeyboardHook()
    {
        if (_keyboardHook == IntPtr.Zero)
        {
            return;
        }

        _ = UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = IntPtr.Zero;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam.ToInt32() == WmKeydown || wParam.ToInt32() == WmSyskeydown))
        {
            var virtualKey = (uint)Marshal.ReadInt32(lParam);
            var hotkeyId = ResolveHookHotkey(virtualKey);
            if (hotkeyId != 0)
            {
                Dispatcher.BeginInvoke(new Action(() => DispatchHotkey(hotkeyId)));
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private int ResolveHookHotkey(uint virtualKey)
    {
        if (MatchesHotkey(_viewModel.Settings.ScreenTranslationHotkey, virtualKey))
        {
            return HotkeyScreenTranslation;
        }

        if (MatchesHotkey(_viewModel.Settings.ScreenTranslationCaptureHotkey, virtualKey))
        {
            return HotkeyScreenTranslationCapture;
        }

        if (MatchesHotkey(_viewModel.Settings.ScreenTranslationRegionHotkey, virtualKey))
        {
            return HotkeyScreenTranslationRegion;
        }

        if (MatchesHotkey(_viewModel.Settings.UiDimmingHotkey, virtualKey))
        {
            return HotkeyUiDimming;
        }

        if (!_viewModel.Settings.StarCitizenHotkeysEnabled)
        {
            return 0;
        }

        if (MatchesHotkey(_viewModel.Settings.StarCitizenServerChangeHotkey, virtualKey))
        {
            return HotkeyServerChange;
        }

        if (MatchesHotkey(_viewModel.Settings.StarCitizenRespawnHotkey, virtualKey))
        {
            return HotkeyRespawn;
        }

        if (MatchesHotkey(_viewModel.Settings.StarCitizenStutterHotkey, virtualKey))
        {
            return HotkeyStutter;
        }

        return 0;
    }

    private static bool MatchesHotkey(string hotkeyText, uint virtualKey)
    {
        if (!TryParseHotkey(hotkeyText, out var modifiers, out var configuredKey) || configuredKey != virtualKey)
        {
            return false;
        }

        var controlDown = IsKeyDown(VkControl);
        var altDown = IsKeyDown(VkMenu);
        var shiftDown = IsKeyDown(VkShift);
        if (((modifiers & ModControl) != 0) != controlDown)
        {
            return false;
        }

        if (((modifiers & ModAlt) != 0) != altDown)
        {
            return false;
        }

        if (((modifiers & 0x0004) != 0) != shiftDown)
        {
            return false;
        }

        return true;
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static void OpenExternalLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Link opening is best-effort only.
        }
    }

    private void BuyMeCoffeeButton_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalLink("https://buymeacoffee.com/anevred");
    }

    private void PayPalButton_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalLink("https://paypal.me/Anevred");
    }

    private void RsiReferralButton_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalLink("https://www.robertsspaceindustries.com/enlist?referral=STAR-4WLN-4RNF");
    }

}
