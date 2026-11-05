using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ValorantOptimizer.App.Presentation;

/// <summary>
/// Drives the boot overlay that covers the shell while the first scan runs.
/// Only <see cref="UIElement.Opacity"/> and render transforms are animated, so the
/// layout pass never runs per frame on the thread that is also starting the scan.
/// </summary>
internal static class BootAnimation
{
    /// <summary>Shortest time the overlay stays up, so a fast scan cannot produce a flash.</summary>
    public static readonly TimeSpan MinimumVisible = TimeSpan.FromMilliseconds(900);

    private const string NoAnimationVariable = "VALORANT_OPTIMIZER_NO_ANIM";
    private const string HoldSecondsVariable = "VALORANT_OPTIMIZER_SPLASH_HOLD";

    /// <summary>Honors the Windows "show animations" setting and an explicit opt-out.</summary>
    public static bool IsEnabled { get; } =
        SystemParameters.ClientAreaAnimation &&
        !string.Equals(
            Environment.GetEnvironmentVariable(NoAnimationVariable),
            "1",
            StringComparison.Ordinal);

    /// <summary>Debug-only: freezes the intro at a timestamp so a frame can be screenshotted.</summary>
    public static bool TryReadHoldSeconds(out double seconds)
    {
        seconds = 0;

        string? raw = Environment.GetEnvironmentVariable(HoldSecondsVariable);
        return !string.IsNullOrWhiteSpace(raw)
               && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
               && seconds >= 0;
    }

    public static void Show(
        Window host,
        FrameworkElement overlay,
        Storyboard intro,
        Storyboard progress,
        TranslateTransform sweep)
    {
        overlay.Visibility = Visibility.Visible;
        overlay.IsHitTestVisible = true;

        if (!IsEnabled)
        {
            // Reduced motion: show the resting frame and a static progress segment.
            overlay.Opacity = 1;
            sweep.X = 0;
            return;
        }

        intro.Begin(host, isControllable: true);
        progress.Begin(host, isControllable: true);
    }

    public static async Task HideAsync(Window host, FrameworkElement overlay, Storyboard exit)
    {
        if (!IsEnabled)
        {
            overlay.Visibility = Visibility.Collapsed;
            overlay.IsHitTestVisible = false;
            return;
        }

        var finished = new TaskCompletionSource();

        void OnCompleted(object? sender, EventArgs e)
        {
            exit.Completed -= OnCompleted;
            finished.TrySetResult();
        }

        exit.Completed += OnCompleted;
        exit.Begin(host, isControllable: true);

        await finished.Task;

        overlay.Visibility = Visibility.Collapsed;
        overlay.IsHitTestVisible = false;
    }

    /// <summary>
    /// Freezes the intro and the progress loop at <paramref name="seconds"/> for screenshot
    /// verification. Mirrors <see cref="Show"/> because the overlay may still be collapsed.
    /// </summary>
    public static void HoldAtFrame(
        Window host,
        FrameworkElement overlay,
        Storyboard intro,
        Storyboard progress,
        double seconds)
    {
        overlay.Visibility = Visibility.Visible;
        overlay.IsHitTestVisible = true;

        var at = TimeSpan.FromSeconds(seconds);

        intro.Begin(host, isControllable: true);
        progress.Begin(host, isControllable: true);

        intro.Pause(host);
        progress.Pause(host);

        intro.Seek(host, at, TimeSeekOrigin.BeginTime);
        progress.Seek(host, at, TimeSeekOrigin.BeginTime);
    }
}
