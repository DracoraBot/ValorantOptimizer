namespace ValorantOptimizer.Core;

/// <summary>A single observed setting, formatted for display.</summary>
public sealed record AssessmentEvidence(string Label, string Value);

/// <summary>
/// Result of inspecting the current machine state for one optimization. Creating an assessment
/// must never change the system.
/// </summary>
public sealed record OptimizationAssessment
{
    public required OptimizationState State { get; init; }

    /// <summary>Short human-readable description of the current state.</summary>
    public required string Headline { get; init; }

    public string? Explanation { get; init; }

    public List<AssessmentEvidence> Evidence { get; init; } = [];

    /// <summary>Why the optimization cannot be applied (Unsupported / ReviewRequired).</summary>
    public string? BlockedReason { get; init; }

    public bool IsActionable => State is OptimizationState.Applied or OptimizationState.NotApplied;

    public static OptimizationAssessment Applied(string headline, string? explanation = null) => new()
    {
        State = OptimizationState.Applied,
        Headline = headline,
        Explanation = explanation,
    };

    public static OptimizationAssessment Available(string headline, string? explanation = null) => new()
    {
        State = OptimizationState.NotApplied,
        Headline = headline,
        Explanation = explanation,
    };

    public static OptimizationAssessment Unsupported(string reason, string? headline = null) => new()
    {
        State = OptimizationState.Unsupported,
        Headline = headline ?? "Not supported on this PC",
        BlockedReason = reason,
    };

    public static OptimizationAssessment Review(string reason, string? headline = null) => new()
    {
        State = OptimizationState.ReviewRequired,
        Headline = headline ?? "Needs review",
        BlockedReason = reason,
    };

    public static OptimizationAssessment Unknown(string reason) => new()
    {
        State = OptimizationState.Unknown,
        Headline = "Could not determine state",
        BlockedReason = reason,
    };
}
