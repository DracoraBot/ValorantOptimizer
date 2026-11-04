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
            new MouseAccelerationOptimization(),
            new WindowsGameModeOptimization(),
            new GameBarCaptureOptimization(),
            new DeliveryOptimizationPeerOptimization(),
            new ValorantGpuPreferenceOptimization(valorantLocator),
            new PowerPlanHighPerformanceOptimization(),
        ];
    }
}
