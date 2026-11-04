namespace ValorantOptimizer.Core;

/// <summary>Result of applying or restoring one optimization.</summary>
public sealed record OperationOutcome
{
    public required bool Success { get; init; }

    /// <summary>True when the engine re-inspected the machine and saw the expected state.</summary>
    public bool Verified { get; init; }

    public required string Message { get; init; }

    /// <summary>Human-readable descriptions of values that were written.</summary>
    public List<string> Changes { get; init; } = [];

    /// <summary>Non-fatal problems the user should see.</summary>
    public List<string> Warnings { get; init; } = [];

    /// <summary>True when a failed apply was rolled back to the captured original state.</summary>
    public bool RolledBack { get; init; }

    public static OperationOutcome Ok(string message, bool verified = false) => new()
    {
        Success = true,
        Verified = verified,
        Message = message,
    };

    public static OperationOutcome Failed(string message, params string[] warnings) => new()
    {
        Success = false,
        Message = message,
        Warnings = warnings.Length == 0 ? [] : [.. warnings],
    };
}
