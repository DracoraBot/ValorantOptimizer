using ValorantOptimizer.Core;
using ValorantOptimizer.Diagnostics;
using ValorantOptimizer.Optimizations;

namespace ValorantOptimizer.App.Services.Preview;

/// <summary>
/// Preview catalog used by `--demo`. It reuses the real descriptors (names, explanations, scopes)
/// but never inspects or changes the machine: every module is wrapped in a synthetic state.
/// </summary>
internal static class PreviewCatalog
{
    public static IReadOnlyList<IOptimization> Create()
    {
        var locator = new ValorantLocator(new RegistryStore(), new ProcessRunner());
        var realModules = OptimizationCatalog.CreateDefault(locator);

        return realModules
            .Select(module => (IOptimization)new PreviewOptimization(
                module.Descriptor,
                StateFor(module.Descriptor.Id),
                HeadlineFor(module.Descriptor.Id),
                "Preview mode: this card's state and evidence are synthetic. Nothing on this PC was read or changed."))
            .ToList();
    }

    private static OptimizationState StateFor(string id) => id switch
    {
        "mouse-acceleration" => OptimizationState.Applied,
        "windows-game-mode" => OptimizationState.Applied,
        "valorant-gpu-preference" => OptimizationState.Applied,
        "mouse-pointer-speed" => OptimizationState.ReviewRequired,
        "delivery-optimization-peer" => OptimizationState.ReviewRequired,
        "nvidia-settings" => OptimizationState.ReviewRequired,
        "memory-overview" => OptimizationState.ReviewRequired,
        "network-overview" => OptimizationState.ReviewRequired,
        "usb-device-power" => OptimizationState.ReviewRequired,
        "storage-overview" => OptimizationState.ReviewRequired,
        "valorant-config" => OptimizationState.ReviewRequired,
        "power-plan-high-performance" => OptimizationState.Unsupported,
        _ => OptimizationState.NotApplied,
    };

    private static string HeadlineFor(string id) => id switch
    {
        "mouse-acceleration" => "Acceleration off (preview)",
        "windows-game-mode" => "Game Mode on (preview)",
        "valorant-gpu-preference" => "High performance (preview)",
        "mouse-pointer-speed" => "Pointer speed 8 of 20 — preference",
        "delivery-optimization-peer" => "Managed by policy (preview)",
        "nvidia-settings" => "NVIDIA driver detected — manual settings",
        "memory-overview" => "15.4 GB in use of 32 GB",
        "network-overview" => "3 adapters · DNS configured",
        "usb-device-power" => "35 USB entries — review only",
        "storage-overview" => "Temp 2.1 GB · drive healthy",
        "valorant-config" => "VALORANT detected — inspect in game",
        "power-plan-high-performance" => "High performance plan is not available",
        _ => "Recommended setting is not applied (preview)",
    };
}
