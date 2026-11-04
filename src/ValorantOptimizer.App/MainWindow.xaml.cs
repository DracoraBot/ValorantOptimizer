using System.Windows;
using System.Windows.Input;
using ValorantOptimizer.App.ViewModels;

namespace ValorantOptimizer.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
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
