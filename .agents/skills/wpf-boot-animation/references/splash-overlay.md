# Splash overlay — full recipe (ValorantOptimizer)

Recommended architecture: **overlay inside `MainWindow`**. No second window, no `ShutdownMode` change, no taskbar/focus problems, and it works with `--demo` screenshots when the splash is disabled.

Files touched:

| File | Change |
|---|---|
| `src/ValorantOptimizer.App/MainWindow.xaml` | overlay markup + storyboard resources |
| `src/ValorantOptimizer.App/MainWindow.xaml.cs` | `Loaded` orchestration, public `BootAnimationEnabled` |
| `src/ValorantOptimizer.App/Presentation/BootAnimation.cs` | new — begin/complete/hold helpers |
| `src/ValorantOptimizer.App/App.xaml.cs` | compute `BootAnimationEnabled` from args/env |

---

## 1. Overlay markup

Add as the **last child of the window's root layout panel** so it paints above the shell. It starts `Collapsed` and is only unhidden by `BootAnimation.Begin`.

```xml
<!-- Boot overlay: last child of the root Grid, above the shell. -->
<Grid x:Name="BootOverlay"
      Panel.ZIndex="9999"
      Background="{StaticResource Bg.Window}"
      Visibility="Collapsed"
      UseLayoutRounding="True">

    <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center"
                RenderTransformOrigin="0.5,0.5">
        <StackPanel.RenderTransform>
            <TransformGroup>
                <ScaleTransform x:Name="BootMarkScale" ScaleX="1" ScaleY="1" />
                <TranslateTransform x:Name="BootWordmarkShift" X="0" Y="0" />
            </TransformGroup>
        </StackPanel.RenderTransform>

        <!-- Swap Data for the real mark, or use an Image whose Source is a Resource pack URI. -->
        <Path x:Name="BootMark"
              Width="72" Height="72"
              Stretch="Uniform"
              Fill="{StaticResource Accent.Default}"
              Data="M 4,44 L 26,4 L 36,20 L 22,44 Z" />

        <TextBlock x:Name="BootWordmark"
                   Text="VALORANT OPTIMIZATION TOOLKIT"
                   Margin="0,18,0,0"
                   FontSize="12"
                   FontWeight="SemiBold"
                   HorizontalAlignment="Center"
                   Foreground="{StaticResource Text.Primary}" />

        <!-- Indeterminate rail: a translated bar inside a clipped border. Never animate Width. -->
        <Border x:Name="BootProgressRail"
                Width="220" Height="3" Margin="0,26,0,0"
                Background="{StaticResource Bg.SurfaceRaised}"
                ClipToBounds="True">
            <Border x:Name="BootProgressBar"
                    Width="80" Height="3"
                    HorizontalAlignment="Left"
                    Background="{StaticResource Accent.Default}">
                <Border.RenderTransform>
                    <TranslateTransform x:Name="BootProgressTranslate" X="-80" />
                </Border.RenderTransform>
            </Border>
        </Border>
    </StackPanel>
</Grid>
```

Base values are the **end frame** (`Opacity` 1, `Scale` 1, `Y` 0). Animations supply `From` and use `FillBehavior="Stop"`, so nothing stays "animated" afterwards.

## 2. Storyboards

Put these in `MainWindow.Resources`.

```xml
<Storyboard x:Key="BootIntroStoryboard">
    <!-- Whole layer: cover the first-frame flash. -->
    <DoubleAnimation Storyboard.TargetName="BootOverlay"
                     Storyboard.TargetProperty="Opacity"
                     From="0" To="1" Duration="0:0:0.12" FillBehavior="Stop">
        <DoubleAnimation.EasingFunction>
            <CubicEase EasingMode="EaseOut" />
        </DoubleAnimation.EasingFunction>
    </DoubleAnimation>

    <!-- Hero: mark settles. -->
    <DoubleAnimation Storyboard.TargetName="BootMarkScale"
                     Storyboard.TargetProperty="ScaleX"
                     From="0.92" To="1" Duration="0:0:0.38" FillBehavior="Stop">
        <DoubleAnimation.EasingFunction>
            <CubicEase EasingMode="EaseOut" />
        </DoubleAnimation.EasingFunction>
    </DoubleAnimation>
    <DoubleAnimation Storyboard.TargetName="BootMarkScale"
                     Storyboard.TargetProperty="ScaleY"
                     From="0.92" To="1" Duration="0:0:0.38" FillBehavior="Stop">
        <DoubleAnimation.EasingFunction>
            <CubicEase EasingMode="EaseOut" />
        </DoubleAnimation.EasingFunction>
    </DoubleAnimation>
    <DoubleAnimation Storyboard.TargetName="BootMark"
                     Storyboard.TargetProperty="Opacity"
                     From="0" To="1" Duration="0:0:0.28" FillBehavior="Stop" />

    <!-- Support: wordmark overlaps the hero, 40-80 ms behind. -->
    <DoubleAnimation Storyboard.TargetName="BootWordmark"
                     Storyboard.TargetProperty="Opacity"
                     From="0" To="1" BeginTime="0:0:0.22" Duration="0:0:0.22" FillBehavior="Stop" />
    <DoubleAnimation Storyboard.TargetName="BootWordmarkShift"
                     Storyboard.TargetProperty="Y"
                     From="8" To="0" BeginTime="0:0:0.22" Duration="0:0:0.34" FillBehavior="Stop">
        <DoubleAnimation.EasingFunction>
            <CubicEase EasingMode="EaseOut" />
        </DoubleAnimation.EasingFunction>
    </DoubleAnimation>

    <!-- Texture: rail appears last. -->
    <DoubleAnimation Storyboard.TargetName="BootProgressRail"
                     Storyboard.TargetProperty="Opacity"
                     From="0" To="1" BeginTime="0:0:0.36" Duration="0:0:0.2" FillBehavior="Stop" />
</Storyboard>

<Storyboard x:Key="BootProgressLoop">
    <!-- Linear is correct for a continuous loop. 30 fps is plenty. -->
    <DoubleAnimation Storyboard.TargetName="BootProgressTranslate"
                     Storyboard.TargetProperty="X"
                     From="-80" To="220"
                     Duration="0:0:1.1"
                     RepeatBehavior="Forever"
                     FillBehavior="Stop" />
</Storyboard>

<Storyboard x:Key="BootExitStoryboard">
    <DoubleAnimation Storyboard.TargetName="BootOverlay"
                     Storyboard.TargetProperty="Opacity"
                     From="1" To="0" Duration="0:0:0.25" FillBehavior="Stop">
        <DoubleAnimation.EasingFunction>
            <CubicEase EasingMode="EaseIn" />
        </DoubleAnimation.EasingFunction>
    </DoubleAnimation>
</Storyboard>
```

## 3. `Presentation/BootAnimation.cs`

```csharp
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace ValorantOptimizer.App.Presentation;

/// <summary>Drives the launch overlay: intro, progress loop, exit, reduced-motion fallback.</summary>
internal static class BootAnimation
{
    public static readonly TimeSpan MinimumVisible = TimeSpan.FromMilliseconds(900);

    public static bool IsEnabled { get; } =
        SystemParameters.ClientAreaAnimation &&
        !string.Equals(
            Environment.GetEnvironmentVariable("VALORANT_OPTIMIZER_NO_ANIM"),
            "1",
            StringComparison.Ordinal);

    public static void Begin(Window host, FrameworkElement overlay,
                             Storyboard intro, Storyboard progress)
    {
        overlay.Visibility = Visibility.Visible;
        overlay.IsHitTestVisible = true;

        if (!IsEnabled)
        {
            overlay.Opacity = 1;
            return;
        }

        intro.Begin(host, isControllable: true);
        progress.Begin(host, isControllable: true);
    }

    public static async Task CompleteAsync(Window host, FrameworkElement overlay,
                                           FrameworkElement progressBar, Storyboard exit)
    {
        if (!IsEnabled)
        {
            overlay.Visibility = Visibility.Collapsed;
            return;
        }

        Storyboard.SetDesiredFrameRate(exit, 60); // exit is short; don't throttle it
        exit.Begin(host, isControllable: true);

        var finished = new TaskCompletionSource();
        exit.Completed += (_, _) => finished.TrySetResult();
        await finished.Task;

        progressBar.Visibility = Visibility.Collapsed;
        overlay.Visibility = Visibility.Collapsed;
        overlay.IsHitTestVisible = false;
    }

    /// <summary>Debug-only: freeze intro + progress at a timestamp so a frame can be screenshotted.</summary>
    public static void HoldAtFrame(Window host, FrameworkElement overlay, Storyboard intro,
                                   Storyboard progress, double seconds)
    {
        // Must mirror Begin(): the overlay starts Collapsed, so a bare Begin() renders nothing.
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
```

`Storyboard.SetDesiredFrameRate` is the code equivalent of `Timeline.DesiredFrameRate`.

## 4. `MainWindow.xaml.cs` orchestration

```csharp
public bool BootAnimationEnabled { get; set; } = true;

private bool _bootStarted;

private async void Window_Loaded(object sender, RoutedEventArgs e)
{
    if (DataContext is not MainViewModel viewModel)
    {
        return;
    }

    Task minimumHold = Task.CompletedTask;

    if (BootAnimationEnabled && !_bootStarted)
    {
        _bootStarted = true;
        BootAnimation.Begin(
            this,
            BootOverlay,
            (Storyboard)Resources["BootIntroStoryboard"],
            (Storyboard)Resources["BootProgressLoop"]);

        minimumHold = Task.Delay(BootAnimation.MinimumVisible);
    }

    try
    {
        await viewModel.InitializeAsync();
    }
    catch (Exception ex)
    {
        MessageBox.Show(
            "The initial scan failed.\n\n" + ex.Message,
            "Scan error",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
    finally
    {
        if (BootAnimationEnabled && _bootStarted)
        {
            await minimumHold;
            await BootAnimation.CompleteAsync(
                this,
                BootOverlay,
                BootProgressRail,
                (Storyboard)Resources["BootExitStoryboard"]);
        }
    }
}
```

When the overlay is hidden the shell underneath must not accept input during boot: the overlay Grid has a `Background`, so it is hit-testable by default. That is what you want until `CompleteAsync` collapses it.

## 5. `App.xaml.cs` flag handling

```csharp
bool headless = HasFlag(e.Args, "--smoke");
bool demo = HasFlag(e.Args, "--demo") || HasFlag(e.Args, "--preview");
bool forceSplash = HasFlag(e.Args, "--splash");
bool forceNoSplash = HasFlag(e.Args, "--no-splash");

bool bootAnimation =
    !headless &&
    !forceNoSplash &&
    BootAnimation.IsEnabled &&
    (forceSplash || !demo);

var window = new MainWindow
{
    DataContext = viewModel,
    BootAnimationEnabled = bootAnimation,
};

static bool HasFlag(string[] args, string name) =>
    args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
```

`--smoke` already returns before any window is created; the guard is there so refactors can't regress it.

## 6. Optional: stroke draw-on

`StrokeDashArray` / `StrokeDashOffset` are in **multiples of `StrokeThickness`**, not pixels — measure and divide.

```csharp
// Flattened geometry turns arcs/curves into polylines, so only two segment types remain.
private static double Measure(Geometry? geometry)
{
    if (geometry is null) return 0;

    PathGeometry path = PathGeometry.CreateFromGeometry(geometry).GetFlattenedPathGeometry();
    double total = 0;

    foreach (PathFigure figure in path.Figures)
    {
        Point current = figure.StartPoint;
        foreach (PathSegment segment in figure.Segments)
        {
            switch (segment)
            {
                case LineSegment line:
                    total += (line.Point - current).Length;
                    current = line.Point;
                    break;
                case PolyLineSegment poly:
                    foreach (Point point in poly.Points)
                    {
                        total += (point - current).Length;
                        current = point;
                    }
                    break;
            }
        }

        if (figure.IsClosed) total += (figure.StartPoint - current).Length;
    }

    return total;
}

// Usage, after InitializeComponent and after the Path has a real StrokeThickness:
double units = Measure(BootMark.Data) / BootMark.StrokeThickness;
BootMark.StrokeDashArray = new DoubleCollection { units, units };
BootMark.StrokeDashOffset = 0; // base value = fully drawn

BootMark.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation
{
    From = units,
    To = 0,
    Duration = TimeSpan.FromMilliseconds(650),
    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
    FillBehavior = FillBehavior.Stop,
});
```

Requires a `Stroke` on the `Path`; a filled mark can't draw on — use a clipped wipe instead.

## 7. Separate `SplashWindow` variant (only if you want a real boot window)

```csharp
ShutdownMode previous = ShutdownMode;          // capture before showing anything
ShutdownMode = ShutdownMode.OnExplicitShutdown;

var splash = new SplashWindow();
splash.Show();

// ... construct MainWindow, initialize ...

MainWindow = window;
window.Show();
window.Activate();
splash.Close();

ShutdownMode = previous;                        // back to OnMainWindowClose
```

`SplashWindow` itself: `WindowStyle="None"`, `ResizeMode="NoResize"`, `ShowInTaskbar="False"`, `WindowStartupLocation="CenterScreen"`, `Background="{StaticResource Bg.Window}"`, `AllowsTransparency="False"`. Close it exactly once, and only after the main window is shown — otherwise the app can exit with a visible-but-dead window.

## 8. Frame verification

```powershell
# The window needs ~1 s to get a MainWindowHandle on a cold start, and the intro is shorter than that.
# So freeze it: hold the intro at 0.35 s, then capture. Ship-gate: this hold must be debug-only.
$env:VALORANT_OPTIMIZER_SPLASH_HOLD = '0.35'
dotnet run --project src\ValorantOptimizer.App -- --demo --splash
tools\capture-window.ps1 -ProcessName ValorantOptimizer -OutputPath artifacts\screenshots\boot-035.png -WaitSeconds 0
```

Wire it next to `BootAnimation.Begin`:

```csharp
if (double.TryParse(Environment.GetEnvironmentVariable("VALORANT_OPTIMIZER_SPLASH_HOLD"), out double holdSeconds))
{
    BootAnimation.HoldAtFrame(
        this,
        BootOverlay,
        (Storyboard)Resources["BootIntroStoryboard"],
        (Storyboard)Resources["BootProgressLoop"],
        holdSeconds);
    return;
}
```

Poll for the handle instead of a fixed sleep if you script it: wait up to 5 s for
`Get-Process -Name ValorantOptimizer | Where-Object MainWindowHandle -ne 0`.

Checklist per frame: mark not distorted, no clipped edges, text crisp at 100 %/150 % DPI, no white/black flash at t=0, final frame equals the static end frame.
