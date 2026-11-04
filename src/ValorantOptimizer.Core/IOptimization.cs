namespace ValorantOptimizer.Core;

/// <summary>Services available to an optimization module while it inspects or changes the machine.</summary>
public interface IOptimizationContext
{
    IRegistryStore Registry { get; }

    IProcessRunner Processes { get; }

    /// <summary>True when the current process already has administrator rights.</summary>
    bool IsElevated { get; }
}

public sealed class OptimizationContext : IOptimizationContext
{
    public OptimizationContext(IRegistryStore registry, IProcessRunner processes, bool isElevated)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Processes = processes ?? throw new ArgumentNullException(nameof(processes));
        IsElevated = isElevated;
    }

    public IRegistryStore Registry { get; }

    public IProcessRunner Processes { get; }

    public bool IsElevated { get; }
}

/// <summary>
/// Contract every optimization implements. The lifecycle is
/// assess -> capture -> apply -> verify (by the engine) and restore -> verify.
/// Implementations must never change state from <see cref="AssessAsync"/> or
/// <see cref="CaptureAsync"/>.
/// </summary>
public interface IOptimization
{
    OptimizationDescriptor Descriptor { get; }

    /// <summary>Reports the current state and applicability without modifying anything.</summary>
    Task<OptimizationAssessment> AssessAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Captures the exact original state needed to undo the optimization.</summary>
    Task<ChangeSet> CaptureAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Applies the recommended configuration using <paramref name="original"/> for safety.</summary>
    Task<OperationOutcome> ApplyAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default);

    /// <summary>Restores the captured original state.</summary>
    Task<OperationOutcome> RestoreAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default);
}
