using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using ValorantOptimizer.App.Services.Preview;
using ValorantOptimizer.App.ViewModels;
using ValorantOptimizer.Core;
using ValorantOptimizer.Diagnostics;
using ValorantOptimizer.Optimizations;

namespace ValorantOptimizer.App.Services;

/// <summary>
/// Composition root. The preview branch is used by the hidden `--demo` flag so the UI can be
/// developed and screenshotted without touching the machine. The live branch wires the real
/// Diagnostics, optimization catalog, per-user stores and the on-demand elevated helper.
/// </summary>
public sealed class AppHost
{
    public MainViewModel CreateMainViewModel(bool demo)
    {
        return demo ? CreatePreviewViewModel() : CreateLiveViewModel();
    }

    private static MainViewModel CreatePreviewViewModel()
    {
        var paths = AppPaths.Default;
        var context = new OptimizationContext(new RegistryStore(), new ProcessRunner(), isElevated: false);
        var modules = PreviewCatalog.Create();
        var backups = new PreviewBackupStore();
        var log = new PreviewActionLog();
        var engine = new OptimizationEngine(modules, backups, log, context);

        return new MainViewModel(
            new PreviewScanService(),
            new PreviewStartupAppScanner(),
            new PreviewSystemMetricsProvider(),
            engine,
            log,
            paths,
            isElevated: false,
            isPreview: true);
    }

    private static MainViewModel CreateLiveViewModel()
    {
        AppPaths paths = AppPaths.Default;
        paths.EnsureCreated();

        bool isElevated = IsAdministrator();
        var registry = new RegistryStore();
        var processes = new ProcessRunner();
        var locator = new CachedValorantLocator(new ValorantLocator(registry, processes));
        var scanService = new ScanService(new SystemScanner(), locator);
        var startupScanner = new StartupAppScanner(registry);
        IReadOnlyList<IOptimization> modules = OptimizationCatalog.CreateDefault(locator);

        var backups = new JsonBackupStore(paths.BackupsDirectory);
        var actionLog = new JsonlActionLog(paths.LogsDirectory);
        var context = new OptimizationContext(registry, processes, isElevated);
        var elevationClient = new ElevatedOperationClient(paths);
        var engine = new OptimizationEngine(modules, backups, actionLog, context, elevationClient);

        return new MainViewModel(
            scanService,
            startupScanner,
            new SystemMetricsProvider(),
            engine,
            actionLog,
            paths,
            isElevated,
            isPreview: false);
    }

    private static bool IsAdministrator()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
