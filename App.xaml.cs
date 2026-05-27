namespace ANEVRED;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

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
}
