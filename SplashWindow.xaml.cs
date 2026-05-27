using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Navigation;

namespace ANEVRED;

public partial class SplashWindow : Window
{
    private readonly TaskCompletionSource _loadedTcs = new();

    public SplashWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            FadeIn();
            _loadedTcs.TrySetResult();
        };
    }

    public async Task PlayExitAnimationAsync()
    {
        await _loadedTcs.Task;

        // Phase 1: split the logo into two visual columns and move them outwards.
        // CenterLogo stays visible behind the split columns so the app mark remains
        // in the middle for a moment after the sides have moved away.
        var splitDuration = TimeSpan.FromMilliseconds(1150);
        var splitEasing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var splitStoryboard = new Storyboard();

        AddDoubleAnimation(splitStoryboard, LogoLeftTransform, "X", 0, -860, splitDuration, splitEasing);
        AddDoubleAnimation(splitStoryboard, LogoRightTransform, "X", 0, 860, splitDuration, splitEasing);
        AddDoubleAnimation(splitStoryboard, LogoScale, "ScaleX", 0.96, 1.02, splitDuration, splitEasing);
        AddDoubleAnimation(splitStoryboard, LogoScale, "ScaleY", 0.96, 1.02, splitDuration, splitEasing);

        await RunStoryboardAsync(splitStoryboard);

        // Phase 2: keep the centered logo visible briefly, then fade the splash away.
        await Task.Delay(950);

        var fadeStoryboard = new Storyboard();
        var fadeEasing = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        AddDoubleAnimation(fadeStoryboard, CenterLogo, "Opacity", 1, 0, TimeSpan.FromMilliseconds(520), fadeEasing);
        AddDoubleAnimation(fadeStoryboard, Root, "Opacity", 1, 0, TimeSpan.FromMilliseconds(720), fadeEasing);

        await RunStoryboardAsync(fadeStoryboard);
    }

    private void FadeIn()
    {
        var duration = TimeSpan.FromMilliseconds(420);
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();
        AddDoubleAnimation(storyboard, Root, "Opacity", 0, 1, duration, easing);
        AddDoubleAnimation(storyboard, LogoScale, "ScaleX", 0.92, 0.96, duration, easing);
        AddDoubleAnimation(storyboard, LogoScale, "ScaleY", 0.92, 0.96, duration, easing);
        storyboard.Begin(this, true);
    }

    private async Task RunStoryboardAsync(Storyboard storyboard)
    {
        var tcs = new TaskCompletionSource();
        storyboard.Completed += (_, _) => tcs.TrySetResult();

        try
        {
            storyboard.Begin(this, true);
        }
        catch
        {
            return;
        }

        // Storyboard.Completed can occasionally fail to fire during early WPF startup
        // or when the owner window state changes. Use a duration-based fallback so
        // the splash never hangs forever.
        var longest = storyboard.Children
            .OfType<Timeline>()
            .Select(t => t.Duration.HasTimeSpan ? t.Duration.TimeSpan : TimeSpan.Zero)
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max();

        var fallback = Task.Delay(longest + TimeSpan.FromMilliseconds(450));
        await Task.WhenAny(tcs.Task, fallback);
    }

    private static void AddDoubleAnimation(
        Storyboard storyboard,
        DependencyObject target,
        string propertyPath,
        double from,
        double to,
        TimeSpan duration,
        IEasingFunction easing)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(duration),
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(propertyPath));
        storyboard.Children.Add(animation);
    }

    private void ReferralLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore link launch failures; splash should never block application startup.
        }

        e.Handled = true;
    }
}
