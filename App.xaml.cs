namespace ANEVRED;

public partial class App : System.Windows.Application
{
    private Services.SingleInstanceService? _singleInstance;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Services.SingleInstanceService();
        if (!_singleInstance.Acquired)
        {
            var localizer = new Services.LocalizationService(Services.SettingsService.DetectLanguage());
            System.Windows.MessageBox.Show(localizer["AlreadyRunning"], "ANEVRED");
            Shutdown();
            return;
        }

        var mainWindow = new MainWindow
        {
            Opacity = 1
        };

        MainWindow = mainWindow;
        mainWindow.Show();

        var splash = new SplashWindow
        {
            Owner = mainWindow,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner
        };

        try
        {
            splash.Show();

            // Keep the startup splash visible long enough to show the brand and referral link.
            await System.Threading.Tasks.Task.Delay(6000);

            // Never allow the splash animation to block startup forever.
            var animationTask = splash.PlayExitAnimationAsync();
            var timeoutTask = System.Threading.Tasks.Task.Delay(3500);
            await System.Threading.Tasks.Task.WhenAny(animationTask, timeoutTask);
        }
        catch
        {
            // Startup must continue even if the splash animation fails.
        }
        finally
        {
            if (splash.IsVisible)
            {
                splash.Close();
            }

            mainWindow.Activate();
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
