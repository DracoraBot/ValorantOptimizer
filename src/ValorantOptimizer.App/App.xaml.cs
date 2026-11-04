using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ValorantOptimizer.App.Services;
using ValorantOptimizer.App.ViewModels;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain", args.ExceptionObject as Exception);

        bool demo = e.Args.Any(argument =>
            string.Equals(argument, "--demo", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, "--preview", StringComparison.OrdinalIgnoreCase));

        if (e.Args.Any(argument => string.Equals(argument, "--smoke", StringComparison.OrdinalIgnoreCase)))
        {
            await RunSmokeTestAsync(e.Args);
            return;
        }

        try
        {
            var host = new AppHost();
            MainViewModel viewModel = host.CreateMainViewModel(demo);
            var window = new MainWindow { DataContext = viewModel };
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            LogCrash("Startup", ex);
            MessageBox.Show(
                "The toolkit could not start.\n\n" + ex.Message,
                "VALORANT Optimization Toolkit",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Headless verification path used by build scripts: runs the real read-only scan and
    /// assessment pipeline, writes a JSON summary, and exits. It never opens a window.
    /// </summary>
    private static async Task RunSmokeTestAsync(string[] args)
    {
        Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        int exitCode = 1;

        try
        {
            var host = new AppHost();
            MainViewModel viewModel = host.CreateMainViewModel(demo: false);
            await viewModel.InitializeAsync();

            string? outputPath = null;
            for (int index = 0; index < args.Length - 1; index++)
            {
                if (string.Equals(args[index], "--smoke", StringComparison.OrdinalIgnoreCase) &&
                    !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    outputPath = args[index + 1];
                }
            }

            outputPath ??= Path.Combine(AppPaths.Default.DataRoot, "scan-smoke.json");
            if (Directory.Exists(outputPath))
            {
                outputPath = Path.Combine(outputPath, "scan-smoke.json");
            }

            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var payload = new
            {
                scannedAt = DateTimeOffset.Now,
                scannedBy = "ValorantOptimizer --smoke",
                hasScanError = viewModel.HasScanError,
                scanError = viewModel.ScanError,
                tiles = viewModel.SummaryTiles.Select(tile => new
                {
                    label = tile.Label,
                    value = tile.Value,
                    subValue = tile.SubValue,
                }),
                cards = viewModel.Cards.Select(card => new
                {
                    id = card.Id,
                    name = card.Name,
                    category = card.Category.ToString(),
                    state = card.State.ToString(),
                    headline = card.Headline,
                    canApply = card.CanApply,
                    hasBackup = card.HasBackup,
                }),
                historyEntries = viewModel.HistoryEntries.Count,
            };

            await File.WriteAllTextAsync(
                outputPath,
                JsonSerializer.Serialize(payload, JsonDefaults.Options));
            exitCode = viewModel.HasScanError ? 2 : 0;
        }
        catch (Exception ex)
        {
            LogCrash("Smoke", ex);
            exitCode = 1;
        }

        Current.Shutdown(exitCode);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("UI", e.Exception);
        System.Windows.MessageBox.Show(
            "Something went wrong in the interface. The action was cancelled.\n\n" + e.Exception.Message,
            "Unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static void LogCrash(string source, Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            AppPaths paths = AppPaths.Default;
            Directory.CreateDirectory(paths.LogsDirectory);
            File.AppendAllText(
                Path.Combine(paths.LogsDirectory, "ui-crash.log"),
                $"[{DateTimeOffset.Now:O}] {source}: {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Crash logging must never mask the original failure.
        }
    }
}
