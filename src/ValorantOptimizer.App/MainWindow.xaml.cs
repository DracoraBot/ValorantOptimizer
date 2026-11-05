using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using ValorantOptimizer.App.Presentation;
using ValorantOptimizer.App.ViewModels;

namespace ValorantOptimizer.App;

public partial class MainWindow : Window
{
    private bool _bootStarted;
    private bool _bootFinished;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Set by <see cref="App"/> before the window is shown. False for preview runs,
    /// <c>--no-splash</c> and headless verification.
    /// </summary>
    public bool ShowBootSequence { get; set; } = true;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        Task minimumVisible = Task.CompletedTask;

        if (ShowBootSequence)
        {
            minimumVisible = StartBootSequence();
        }
        else
        {
            BootOverlay.Visibility = Visibility.Collapsed;
        }

        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            // Finish the boot first, otherwise the dialog would sit behind the overlay.
            await FinishBootAsync(minimumVisible);
            MessageBox.Show(
                "The initial scan failed.\n\n" + ex.Message,
                "Scan error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        await FinishBootAsync(minimumVisible);
    }

    private async Task FinishBootAsync(Task minimumVisible)
    {
        if (!_bootStarted || _bootFinished)
        {
            return;
        }

        _bootFinished = true;
        await minimumVisible;

        if (!IsLoaded)
        {
            // The window was closed while the scan was still running.
            return;
        }

        await BootAnimation.HideAsync(
            this,
            BootOverlay,
            (Storyboard)Resources["BootExitStoryboard"]);
    }

    private Task StartBootSequence()
    {
        _bootStarted = true;
        var intro = (Storyboard)Resources["BootIntroStoryboard"];

        if (BootAnimation.TryReadHoldSeconds(out double holdSeconds))
        {
            // Debug capture: leave the frozen frame on screen instead of finishing the boot.
            _bootFinished = true;
            BootAnimation.HoldAtFrame(
                this,
                BootOverlay,
                intro,
                (Storyboard)Resources["BootProgressLoop"],
                holdSeconds);
            return Task.CompletedTask;
        }

        BootAnimation.Show(
            this,
            BootOverlay,
            intro,
            (Storyboard)Resources["BootProgressLoop"],
            BootProgressSweep);

        return Task.Delay(BootAnimation.MinimumVisible);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (WindowState == WindowState.Normal && e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // The mouse was released while the drag started.
            }
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeButton is null)
        {
            return;
        }

        bool maximized = WindowState == WindowState.Maximized;
        if (MaximizeGlyph is not null)
        {
            MaximizeGlyph.Text = maximized ? "\uE923" : "\uE922";
        }

        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
        WindowFrame.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainViewModel viewModel)
        {
            viewModel.DismissTopLayer();
            e.Handled = true;
        }
    }
}
