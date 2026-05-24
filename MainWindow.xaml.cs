using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ZestResourceOptimizer.Models;
using Forms = System.Windows.Forms;
using ZestResourceOptimizer.ViewModels;

namespace ZestResourceOptimizer;

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
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkShift = 0x10;

    private readonly MainViewModel _viewModel;
    private readonly Services.ScreenTranslationService _screenTranslationService;
    private readonly System.Windows.Threading.DispatcherTimer _screenTranslationTimer = new();
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
    private IntPtr _keyboardHook;
    private DateTime _lastTranslationToggle = DateTime.MinValue;
    private DateTime _lastTranslationCapture = DateTime.MinValue;

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
        BuildTrayMenu();
        SourceInitialized += OnSourceInitialized;
        _screenTranslationTimer.Interval = TimeSpan.FromSeconds(30);
        _screenTranslationTimer.Tick += async (_, _) => await UpdateScreenTranslationAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterGlobalHotkeys();
        UninstallKeyboardHook();
        _source?.RemoveHook(WndProc);
        _screenTranslationTimer.Stop();
        _screenTranslationService.Dispose();
        _translationOverlay?.Close();
        _notifyIcon?.Dispose();
        _trayMenu?.Dispose();
        _viewModel.Settings.PropertyChanged -= SettingsPropertyChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void ApplyLocalizedColumnHeaders()
    {
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
            SetBrush("SidebarBackgroundBrush", "#EAF0F6");
            SetBrush("PanelBackgroundBrush", "#FFFFFF");
            SetBrush("PanelAltBackgroundBrush", "#EAF0F6");
            SetBrush("PanelHoverBrush", "#DCE7F3");
            SetBrush("PrimaryTextBrush", "#10151D");
            SetBrush("SecondaryTextBrush", "#526173");
            SetBrush("DisabledTextBrush", "#7B8794");
            SetBrush("BorderBrushSoft", "#CDD6E0");
            SetBrush("GraphFillBrush", "#F8FAFC");
            SetBrush("TableHeaderBrush", "#E2EAF4");
            SetBrush("AccentBrush", "#3B82F6");
            SetBrush("RamAccentBrush", "#22C55E");
            SetBrush("CpuAccentBrush", "#F59E0B");
            SetBrush("GpuAccentBrush", "#A855F7");
            SetBrush("VramAccentBrush", "#3B82F6");
            SetBrush("FrametimeAccentBrush", "#F97316");
            SetBrush("CriticalBrush", "#EF4444");
        }
        else
        {
            SetBrush("WindowBackgroundBrush", "#0B111A");
            SetBrush("SidebarBackgroundBrush", "#09101A");
            SetBrush("PanelBackgroundBrush", "#121A26");
            SetBrush("PanelAltBackgroundBrush", "#151F2E");
            SetBrush("PanelHoverBrush", "#1B2940");
            SetBrush("PrimaryTextBrush", "#F2F6FF");
            SetBrush("SecondaryTextBrush", "#9BA8BC");
            SetBrush("DisabledTextBrush", "#667085");
            SetBrush("BorderBrushSoft", "#263448");
            SetBrush("GraphFillBrush", "#0E1622");
            SetBrush("TableHeaderBrush", "#182437");
            SetBrush("AccentBrush", "#3B82F6");
            SetBrush("RamAccentBrush", "#22C55E");
            SetBrush("CpuAccentBrush", "#F59E0B");
            SetBrush("GpuAccentBrush", "#A855F7");
            SetBrush("VramAccentBrush", "#3B82F6");
            SetBrush("FrametimeAccentBrush", "#F97316");
            SetBrush("CriticalBrush", "#EF4444");
        }
    }

    private static void SetBrush(string key, string color)
    {
        System.Windows.Application.Current.Resources[key] = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }

    private void BuildTrayMenu()
    {
        _trayMenu?.Dispose();
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add(_viewModel.L["Open"], null, (_, _) =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
        _trayMenu.Items.Add(_viewModel.L["Exit"], null, (_, _) => Close());

        _notifyIcon ??= new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
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
        WindowState = WindowState.Normal;
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
        var registered = new List<string>();
        var failed = new List<string>();
        RegisterConfiguredHotkey(HotkeyScreenTranslation, _viewModel.Settings.ScreenTranslationHotkey, registered, failed);
        RegisterConfiguredHotkey(HotkeyScreenTranslationCapture, _viewModel.Settings.ScreenTranslationCaptureHotkey, registered, failed);
        RegisterConfiguredHotkey(HotkeyScreenTranslationRegion, _viewModel.Settings.ScreenTranslationRegionHotkey, registered, failed);

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
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
        {
            return IntPtr.Zero;
        }

        handled = true;
        switch (wParam.ToInt32())
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
        }

        return IntPtr.Zero;
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
            or nameof(AppSettings.ScreenTranslationRegionHotkey))
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
    }

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
        _translationOverlay.SetText("Uebersetzung laeuft...", string.Empty);
        if (!_translationOverlay.IsVisible)
        {
            _translationOverlay.Show();
        }

        _translationOverlay.Topmost = false;
        _translationOverlay.Topmost = true;
        _viewModel.AddDiagnosticLog("Info", "ScreenTranslation: OCR done, waiting for local model.");
    }

    private void TryChooseTranslationRegion()
    {
        _viewModel.AddDiagnosticLog("Info", "ScreenTranslation: region selection requested.");
        ChooseTranslationRegion();
    }

    private void StartScreenTranslation()
    {
        _screenTranslationCancellation?.Cancel();
        _screenTranslationCancellation?.Dispose();
        _screenTranslationCancellation = new CancellationTokenSource();
        var runId = ++_screenTranslationRunId;
        _translationOverlay ??= new TranslationOverlayWindow();
        _translationOverlay.SetRegion(CurrentTranslationRegion());
        _translationOverlay.SetText(string.Empty, "Live-Uebersetzung aktiv.");
        _translationOverlay.Show();
        _translationOverlay.Activate();
        _translationOverlay.Topmost = false;
        _translationOverlay.Topmost = true;
        _screenTranslationTimer.Start();
        _viewModel.AddDiagnosticLog("Info", $"ScreenTranslation: overlay start visible={_translationOverlay.IsVisible}, enabled={_viewModel.Settings.ScreenTranslationEnabled}, region={CurrentTranslationRegion()}.");
        _ = WarmUpThenUpdateScreenTranslationAsync(runId, _screenTranslationCancellation.Token);
    }

    private async Task WarmUpThenUpdateScreenTranslationAsync(int runId, CancellationToken cancellationToken)
    {
        await WarmUpScreenTranslationModelAsync(runId, cancellationToken);
        if (IsCurrentScreenTranslationRun(runId, cancellationToken))
        {
            await UpdateScreenTranslationAsync(cancellationToken, runId);
        }
    }

    private async Task WarmUpScreenTranslationModelAsync(int runId, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsCurrentScreenTranslationRun(runId, cancellationToken))
            {
                return;
            }

            _viewModel.AddDiagnosticLog("Info", "ScreenTranslation: local model warmup started.");
            await _screenTranslationService.WarmUpAsync(
                _viewModel.Settings.ScreenTranslationTargetLanguage,
                _viewModel.Settings.ScreenTranslationEngine,
                cancellationToken);
            if (IsCurrentScreenTranslationRun(runId, cancellationToken))
            {
                _viewModel.AddDiagnosticLog("Info", "ScreenTranslation: local model warmup finished.");
            }
        }
        catch (OperationCanceledException)
        {
            _viewModel.AddDiagnosticLog("Info", "ScreenTranslation: local model warmup cancelled.");
        }
        catch (Exception ex)
        {
            _viewModel.AddDiagnosticLog("Warn", "ScreenTranslation: local model warmup failed: " + ex.Message);
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
                _translationOverlay.SetText(string.Empty, "Uebersetzungsbereich liegt ausserhalb des sichtbaren Bildschirms.");
                _viewModel.AddDiagnosticLog("Warn", $"ScreenTranslation: capture region invalid after screen clamp: {captureRegion}.");
                return;
            }

            var wasVisible = _translationOverlay.IsVisible;
            ScreenTranslationResult result;
            _viewModel.AddDiagnosticLog("Info", $"ScreenTranslation: capture with selected overlay text paused, visible={wasVisible}.");

            try
            {
                _translationOverlay.SetCaptureMode(true);
                await Task.Delay(80);
                if (!IsCurrentScreenTranslationRun(activeRunId, cancellationToken))
                {
                    return;
                }

                result = await _screenTranslationService.TranslateRegionAsync(
                    captureRegion,
                    _viewModel.Settings.ScreenTranslationTargetLanguage,
                    _viewModel.Settings.ScreenTranslationEngine,
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
                    _translationOverlay.Topmost = false;
                    _translationOverlay.Topmost = true;
                    _viewModel.AddDiagnosticLog("Info", $"ScreenTranslation: overlay refreshed visible={_translationOverlay.IsVisible}.");
                }
            }

            if (!IsCurrentScreenTranslationRun(activeRunId, cancellationToken))
            {
                return;
            }

            _translationOverlay.SetText(result.TranslatedText, result.Status);
            _viewModel.AddDiagnosticLog("Info", $"ScreenTranslation: update end visible={_translationOverlay.IsVisible}, textChars={result.TranslatedText.Length}, status={result.Status}");
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
        ChooseTranslationRegion();
    }

    private void ChooseTranslationRegion()
    {
        var wasEnabled = _viewModel.Settings.ScreenTranslationEnabled;
        _viewModel.AddDiagnosticLog("Info", $"ScreenTranslation: choose region start, wasEnabled={wasEnabled}.");
        StopScreenTranslation();
        var selector = new RegionSelectionWindow { Owner = this };
        if (selector.ShowDialog() == true && selector.SelectedRegion is { } region)
        {
            _viewModel.AddDiagnosticLog("Info", $"ScreenTranslation: region selected {region}.");
            _viewModel.Settings.ScreenTranslationLeft = region.Left;
            _viewModel.Settings.ScreenTranslationTop = region.Top;
            _viewModel.Settings.ScreenTranslationWidth = region.Width;
            _viewModel.Settings.ScreenTranslationHeight = region.Height;
        }

        if (wasEnabled)
        {
            StartScreenTranslation();
        }

        _viewModel.AddDiagnosticLog("Info", "ScreenTranslation: choose region end.");
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
            if (MatchesHotkey(_viewModel.Settings.ScreenTranslationHotkey, virtualKey))
            {
                Dispatcher.BeginInvoke(new Action(TryToggleScreenTranslation));
            }
            else if (MatchesHotkey(_viewModel.Settings.ScreenTranslationCaptureHotkey, virtualKey))
            {
                Dispatcher.BeginInvoke(new Action(TryCaptureScreenTranslation));
            }
            else if (MatchesHotkey(_viewModel.Settings.ScreenTranslationRegionHotkey, virtualKey))
            {
                Dispatcher.BeginInvoke(new Action(TryChooseTranslationRegion));
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
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
}

