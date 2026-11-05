---
name: wpf-boot-animation
description: Use when adding, tuning, or debugging the launch/boot animation for the ValorantOptimizer WPF desktop app — animated splash overlay or splash window, Storyboard choreography, splash-to-MainWindow handoff, reduced-motion fallback, and frame-by-frame verification. Triggers on "boot animation", "splash screen", "startup animation", "launch animation", "intro sequence", "opening animation".
---

# Boot animation — ValorantOptimizer (WPF)

Add a boot animation to a **C# / WPF (.NET 10, `net10.0-windows`)** desktop app that ships as a single-file `.exe`. Work is XAML-first: hand-written `Storyboard`s, no animation libraries, no new NuGet packages.

> **Already implemented** (2026-11-04): the overlay lives in `MainWindow.xaml` (`BootOverlay` + the three `Boot*` storyboards), driven by `Presentation/BootAnimation.cs` and `MainWindow.Window_Loaded`, and gated by `App.ShouldShowBootSequence`. Tune that; don't add a second splash.

## Scope gate — confirm before doing these

- Adding a NuGet package (Lottie, SkiaSharp, etc.) or an external media file.
- Changing `MainWindow` chrome, sizing, or the custom title bar.
- Making the boot sequence required for `--smoke` or `--demo`/`--preview` runs.
- Shipping a pre-rendered video: changing the release pipeline to ship extra assets.

Otherwise proceed; these are normal edits.

## 1. Facts you must verify, not assume

Repo root: `ValorantOptimizer.sln`. App project: `src/ValorantOptimizer.App/`.

| Fact | Value |
|---|---|
| Project | `ValorantOptimizer.App.csproj` — `WinExe`, `net10.0-windows`, `UseWPF`, `AssemblyName` = `ValorantOptimizer` |
| Icon | `assets/app.ico` (`<ApplicationIcon>`) |
| Palette | `src/ValorantOptimizer.App/Styles/Colors.xaml` — `Bg.Window` `#0B0D12`, `Accent.Default` `#FF4655`, `Text.Primary`, `Text.Secondary` |
| Theme | `App.xaml` merges `Styles/Colors.xaml`, `Typography.xaml`, `Controls.xaml` |
| Shell | `MainWindow` = `WindowStyle="None"` + `WindowChrome`, 1320×860, `ShutdownMode="OnMainWindowClose"` |
| Existing motion | Hand-written `DoubleAnimation` in `Views/ToastView.xaml`, `DetailDrawer.xaml`, `OverviewView.xaml` — match that idiom |
| Views | `Views/*.xaml` (Overview, Monitoring, History, Startup, DetailDrawer, ConfirmDialog, Toast) |

Use `{StaticResource ...}` for every color and font. **Never hardcode hex** — the app already themes through `Colors.xaml`.

## 2. Startup wiring — the parts that bite

Read `App.xaml.cs` before editing. Current flow:

1. `App.xaml` has **no `StartupUri`**; `ShutdownMode="OnMainWindowClose"`.
2. `App.OnStartup` (already `async void`) constructs `AppHost` → `MainViewModel` → `MainWindow`, assigns `MainWindow`, calls `Show()`.
3. `MainWindow.Window_Loaded` awaits `viewModel.InitializeAsync()` — the real scan, roughly 1–4 s.

Hidden modes that must keep working:

- `--smoke <path>` — headless verification; **never opens a window**. A splash here is a bug.
- `--demo` / `--preview` — in-memory preview services used for screenshots and UI Automation (`tools/capture-window.ps1`, `docs/VERIFICATION.md`). Default to **skipping the splash** here so automation targets `MainWindow`, and opt in with `--splash` when you need to capture frames.
- Add `VALORANT_OPTIMIZER_NO_ANIM=1` as an escape hatch (reduced motion / debugging).

**`ShutdownMode` trap:** the first window shown becomes `Application.MainWindow`. With `ShutdownMode="OnMainWindowClose"`, closing that splash **shuts the app down**. Two safe designs:

- **Overlay (recommended).** A full-window `Grid` layer inside `MainWindow` (highest `Panel.ZIndex`). No second window, no shutdown-mode change, no taskbar/focus problems.
- **Separate `SplashWindow`.** Set `ShutdownMode = ShutdownMode.OnExplicitShutdown` *before* showing it, assign `Application.MainWindow = mainWindow` when main is ready, close the splash, then restore `ShutdownMode.OnMainWindowClose`.

## 3. Design the sequence before touching XAML

Decide these and write them down; animation that "just happens" reads cheap.

- **Budget.** Fast init: total ≤ **1.6 s**. Slow init: **1.2 s** intro, then indeterminate progress until ready, **0.25 s** exit. Floor **0.8 s** (no flash); never pad more than ~0.4 s beyond actual init.
- **One technique, not four.** Pick exactly one reveal: fade+scale settle, clipped wipe, or build-on stagger (per-logo parts). Never draw *and* wipe *and* bounce.
- **Static end frame.** The boot's final frame must equal the app's resting brand state, and it must still look right if animation is disabled.
- **Motion language.** Valorant = angular, fast, precise. Entries decelerate (ease-out), exits accelerate (ease-in), no floaty 2 s reveals, no cartoon bounce on a system tool.

Suggested skeleton (adjust, don't cargo-cult):

| t (ms) | Beat |
|---:|---|
| 0 | Mark at `Opacity 0`, `Scale 0.92` — set as **base XAML values**, not in `Loaded` |
| 80–480 | Mark settles: `Opacity 0→1`, `Scale 0.92→1`, `CubicEase EaseOut` |
| 300–700 | Wordmark / subtitle gets `BeginTime="0:0:0.3"`, `Translate Y 8→0` |
| 600+ | Progress rail fades in; indeterminate sweep runs at 30 fps |
| ready | Exit `Opacity 1→0`, `Scale 1→1.02`, 250 ms `EaseIn` |

Keep the **1/3 rule**: with 3+ elements on screen, no more than ~1/3 should be moving at once.

## 4. WPF implementation rules

**Animate these:** `Opacity`, `RenderTransform` (`TranslateTransform`, `ScaleTransform`, `RotateTransform`), `StrokeDashOffset`, `Brush.Color`.
**Never animate these:** `Width`, `Height`, `Margin`, `Padding`, `CornerRadius`, `FontSize` — each triggers layout/re-render per frame.

- Set the **final value as the base property value in XAML**, animate `From` (or `From`/`To`) with `FillBehavior="Stop"`. Held animation clocks keep a "dirty" animated value forever and fight later updates.
- Freeze shared resources: `xmlns:po="http://schemas.microsoft.com/winfx/2006/xaml/presentation/options"` + `po:Freeze="True"` on `SolidColorBrush` and `Geometry`.
- `RenderTransformOrigin="0.5,0.5"` on anything scaled or rotated.
- Storyboard lives in `Window.Resources`; start from an `EventTrigger` (`RoutedEvent="FrameworkElement.Loaded"`) or `BeginStoryboard` in code; use the `Completed` event for the handoff.
- Indeterminate progress = translate a bar inside a `ClipToBounds="True"` `Border`, `RepeatBehavior="Forever"` + `AutoReverse="True"`, and cap it: `<Storyboard Timeline.DesiredFrameRate="30">`.
- **No `Thread.Sleep`, no blocking `Dispatcher.Invoke`.** Init is async; `await` it while the storyboard runs on the render thread's clock.
- Splash window, if you use one: `WindowStyle="None"`, `ResizeMode="NoResize"`, `ShowInTaskbar="False"`, `WindowStartupLocation="CenterScreen"`, `Background="{StaticResource Bg.Window}"`, `UseLayoutRounding="True"`. Keep `AllowsTransparency="False"` (layered windows cost performance); fake rounded corners with a `Border` inside an opaque window.
- Don't animate the window's own `Opacity` — animate the root content layer.

## 5. Reduced motion (not optional)

```csharp
private static bool AnimationsEnabled =>
    SystemParameters.ClientAreaAnimation &&
    Environment.GetEnvironmentVariable("VALORANT_OPTIMIZER_NO_ANIM") != "1";
```

Reduced path: show the end-frame mark + progress immediately, at most a 150 ms cross-fade, and still signal when loading finishes. Never remove progress feedback — only remove motion.

## 6. Handoff

Overlay:

1. Overlay visible at `Loaded`, hit-testable so clicks don't reach the shell underneath.
2. Start the intro storyboard; run `InitializeAsync()` concurrently with a **minimum-hold** delay (`Task.WhenAll`).
3. Fade the overlay out, then `Visibility="Collapsed"` and `IsHitTestVisible="False"`.
4. Guard with a `bool` so double `Loaded`/re-entry can't run it twice; `try/finally` so a failed scan still ends the boot.

Separate window: `mainWindow.Show()` → `mainWindow.Activate()` → `splash.Close()` **in that order**, then restore `ShutdownMode`.

## 7. Verify — evidence, not vibes

```powershell
dotnet build ValorantOptimizer.sln -c Release
dotnet test  ValorantOptimizer.sln -c Release --no-build
dotnet run --project src\ValorantOptimizer.App -- --demo
```

- Capture the splash with `tools\capture-window.ps1` (PrintWindow). A cold start needs roughly **1 s** before the process exposes `MainWindowHandle` — longer than the intro — so add a debug hold (`VALORANT_OPTIMIZER_SPLASH_HOLD=<seconds>`, implemented with `Storyboard.Pause` + `Seek`) and screenshot **t≈0 / mid / end**.
- Check every frame: mark undistorted, nothing clipped, text crisp, no black flash on the **first** frame (means state was set in `Loaded` instead of XAML), final frame is the clean mark.
- `dist` single-file publish still boots: assets must be `Resource`/pack-URI, never loose files next to the `.exe`.
- `--smoke` still exits 0 **without** a window; `--demo` automation still finds `MainWindow`.
- No new entry in the crash log under `AppPaths.Default.LogsDirectory` (`ui-crash.log`).

## 8. Acceptance checklist

- [ ] One reveal technique; total ≤ 1.6 s when init is fast.
- [ ] Only `Opacity` / `RenderTransform` / `StrokeDashOffset` animated.
- [ ] Colors via `Colors.xaml`; brushes/geometries frozen.
- [ ] Static end frame correct with motion disabled.
- [ ] `--smoke`, `--demo`, `--preview` behavior unchanged.
- [ ] Overlay/window closes exactly once; app never exits early via `ShutdownMode`.
- [ ] Frames captured at start/mid/end and inspected.
- [ ] Release build + full test suite pass; single-file publish still animates.

## 9. Pitfalls

1. **`OnMainWindowClose` + splash window** → app quits when the splash closes. See §2.
2. **Animating `Width`/`Height`** → layout thrash and jank at exactly the moment the CPU is busiest.
3. **State set in `Loaded`** → one frame of wrong layout; set initial values in XAML.
4. **`FillBehavior="HoldEnd"` left running** → later property updates silently ignored.
5. **`StrokeDashArray` units** are multiples of stroke thickness, not pixels — divide the measured path length by `StrokeThickness` or the draw-on jumps.
6. **Second window steals focus / shows in taskbar+Alt-Tab** → boot feels broken.
7. **Splash runs for `--smoke`** → headless verification hangs or fails.
8. **Hard-coded `#FF4655`** → breaks the palette contract; use `{StaticResource Accent.Default}`.
9. **Faking letter-spacing per character** — WPF has no `LetterSpacing`; don't build a per-glyph stagger for a wordmark that ships in a tool.
10. **First-run JIT cost** — measure cold start; if the first animation stutters, trim the intro instead of adding frames.

## 10. Reference

- `references/splash-overlay.md` — copy-paste overlay XAML, storyboard, code-behind, hold-at-frame debugging, draw-on length helper, and the `App.xaml.cs` skip logic.

Related skills: `logo-animation` (reveal technique and end-frame discipline), `animation-principles` (duration/easing/stagger numbers), `wpf-best-practices` (repo conventions, publish, DI).
