using ValorantOptimizer.Core;
using ValorantOptimizer.Diagnostics;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// The explicit module catalog. Adding an optimization is a one-line change here plus its module
/// and tests; the engine validates ids, metadata and scopes when it starts.
/// </summary>
public static class OptimizationCatalog
{
    public static IReadOnlyList<IOptimization> CreateDefault(IValorantLocator valorantLocator)
    {
        ArgumentNullException.ThrowIfNull(valorantLocator);

        return
        [
            // Mouse
            new MouseAccelerationOptimization(),
            new MousePointerSpeedReview(),

            // Windows
            new WindowsGameModeOptimization(),
            new GameBarCaptureOptimization(),
            new WindowsBackgroundAppsOptimization(),
            new DeliveryOptimizationPeerOptimization(),

            // CPU and power
            new CpuPowerThrottlingOptimization(),
            new CpuCoreParkingOptimization(),
            new CpuSchedulingOptimization(),
            new PowerPlanHighPerformanceOptimization(),

            // GPU
            new HagsOptimization(),
            new NvidiaSettingsReview(),

            // Memory
            new MemoryOverviewReview(),

            // Network
            new NetworkAdapterPowerOptimization(),
            new NetworkOverviewReview(),

            // USB
            new UsbSelectiveSuspendOptimization(),
            new UsbDevicePowerReview(),

            // Storage
            new StorageOverviewReview(),

            // VALORANT
            new ValorantGpuPreferenceOptimization(valorantLocator),
            new ValorantAppCompatOptimization(valorantLocator),
            new ValorantConfigReview(valorantLocator),
        ];
    }
}
