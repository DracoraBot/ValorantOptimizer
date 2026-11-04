namespace ValorantOptimizer.Core;

public enum ElevatedOperationKind
{
    Apply,
    Restore,
}

/// <summary>
/// One privileged operation, handed to the elevated helper through a temp file. The helper only
/// executes known optimization ids and validates restore entries against declared scopes.
/// </summary>
public sealed record ElevatedOperationRequest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string RequestId { get; init; }

    public required string OptimizationId { get; init; }

    public required ElevatedOperationKind Operation { get; init; }

    public required ChangeSet ChangeSet { get; init; }

    public static ElevatedOperationRequest Create(string optimizationId, ElevatedOperationKind operation, ChangeSet changeSet) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        OptimizationId = optimizationId,
        Operation = operation,
        ChangeSet = changeSet,
    };
}

public sealed record ElevatedOperationResponse
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string RequestId { get; init; }

    public required bool Success { get; init; }

    public bool Verified { get; init; }

    public bool RolledBack { get; init; }

    public string? Message { get; init; }

    /// <summary>Technical error detail for the log; not shown verbatim in the primary UI.</summary>
    public string? Error { get; init; }

    public List<string> Changes { get; init; } = [];

    public List<string> Warnings { get; init; } = [];

    public OperationOutcome ToOutcome()
    {
        if (Success)
        {
            return new OperationOutcome
            {
                Success = true,
                Verified = Verified,
                Message = Message ?? "Change applied.",
                Changes = Changes,
                Warnings = Warnings,
                RolledBack = RolledBack,
            };
        }

        return new OperationOutcome
        {
            Success = false,
            Verified = false,
            Message = Message ?? "The elevated helper reported a failure.",
            Changes = Changes,
            Warnings = Warnings,
            RolledBack = RolledBack,
        };
    }
}

/// <summary>Client side of the elevated helper protocol, implemented by the UI process.</summary>
public interface IElevatedOperationClient
{
    Task<ElevatedOperationResponse> ExecuteAsync(
        ElevatedOperationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class ElevationCancelledException : Exception
{
    public ElevationCancelledException(string message) : base(message) { }
}

public sealed class ElevationClientException : Exception
{
    public ElevationClientException(string message) : base(message) { }
    public ElevationClientException(string message, Exception inner) : base(message, inner) { }
}
