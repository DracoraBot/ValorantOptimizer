namespace ValorantOptimizer.Core;

public enum OptimizationCategory
{
    Mouse,
    Windows,
    CpuPower,
    Gpu,
    Memory,
    Network,
    Usb,
    Storage,
    Valorant,
    Startup,
}

public enum RiskLevel
{
    /// <summary>Broadly safe, reversible, well understood.</summary>
    Safe,

    /// <summary>System-wide or policy-level change; explicit confirmation required.</summary>
    Advanced,

    /// <summary>Hardware/driver dependent or disputed; requires explanation and consent.</summary>
    Experimental,
}

public enum RestartRequirement
{
    None,
    SignOut,
    Reboot,
}

public enum OptimizationState
{
    /// <summary>The recommended configuration is currently in effect.</summary>
    Applied,

    /// <summary>The recommended configuration is not in effect and can be applied.</summary>
    NotApplied,

    /// <summary>This PC/OS/game install does not support the optimization.</summary>
    Unsupported,

    /// <summary>Applicability or ownership needs user review before enabling.</summary>
    ReviewRequired,

    /// <summary>Could not be determined (for example a scan failed).</summary>
    Unknown,
}

/// <summary>
/// Static description of a single optimization. One instance per module; immutable at runtime.
/// </summary>
public sealed record OptimizationDescriptor
{
    /// <summary>Stable, filename-safe identifier (lowercase, digits, '.', '-').</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required OptimizationCategory Category { get; init; }

    public required RiskLevel Risk { get; init; }

    /// <summary>One sentence shown on the grid card.</summary>
    public required string Summary { get; init; }

    /// <summary>Plain-language description of what changes on disk.</summary>
    public required string WhatItChanges { get; init; }

    /// <summary>Why a player might want this; no promised FPS numbers.</summary>
    public required string Why { get; init; }

    /// <summary>Hardware/OS/driver constraints, or null when broadly applicable.</summary>
    public string? Compatibility { get; init; }

    /// <summary>Short label describing the applied state, e.g. "Acceleration off".</summary>
    public string StateWhenApplied { get; init; } = "Applied";

    public RestartRequirement Restart { get; init; } = RestartRequirement.None;

    /// <summary>True when writes require an elevated helper process.</summary>
    public bool RequiresElevation { get; init; }

    /// <summary>Registry locations that apply/undo may touch. Used to validate restore data.</summary>
    public List<RegistryScope> ManagedScopes { get; init; } = [];

    /// <summary>Additional caveats surfaced in the details panel.</summary>
    public List<string> Notes { get; init; } = [];
}
