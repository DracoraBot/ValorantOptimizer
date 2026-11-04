using System.Collections.Concurrent;

namespace ValorantOptimizer.Core;

/// <summary>Descriptor + live assessment + optional restore data for one optimization.</summary>
public sealed record OptimizationStatus(IOptimization Optimization, OptimizationAssessment Assessment, ChangeSet? Backup)
{
    public bool HasBackup => Backup is not null;

    public bool CanApply => Assessment.State == OptimizationState.NotApplied;

    public bool CanUndo => Assessment.State == OptimizationState.Applied && Backup is not null;
}

/// <summary>
/// Orchestrates the optimization lifecycle and owns all safety guarantees:
/// capture -> persist backup -> apply (optionally elevated) -> verify -> log,
/// with automatic rollback when an apply fails.
/// </summary>
public sealed class OptimizationEngine
{
    private readonly Dictionary<string, IOptimization> _byId;
    private readonly IBackupStore _backups;
    private readonly IActionLog _log;
    private readonly IOptimizationContext _context;
    private readonly IElevatedOperationClient? _elevation;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public OptimizationEngine(
        IEnumerable<IOptimization> optimizations,
        IBackupStore backups,
        IActionLog log,
        IOptimizationContext context,
        IElevatedOperationClient? elevatedOperationClient = null)
    {
        ArgumentNullException.ThrowIfNull(optimizations);
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _elevation = elevatedOperationClient;

        List<IOptimization> list = optimizations.ToList();
        IReadOnlyList<string> errors = OptimizationCatalogValidator.Validate(list);
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid optimization catalog: " + string.Join(" ", errors), nameof(optimizations));
        }

        Optimizations = list;
        _byId = list.ToDictionary(optimization => optimization.Descriptor.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<IOptimization> Optimizations { get; }

    public IOptimization? Find(string optimizationId) =>
        _byId.TryGetValue(optimizationId, out IOptimization? optimization) ? optimization : null;

    /// <summary>Assesses every optimization sequentially and reports each result as it completes.</summary>
    public async Task<IReadOnlyList<OptimizationStatus>> AssessAllAsync(
        IProgress<OptimizationStatus>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<OptimizationStatus>(Optimizations.Count);
        foreach (IOptimization optimization in Optimizations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OptimizationStatus status = await BuildStatusAsync(optimization, cancellationToken).ConfigureAwait(false);
            results.Add(status);
            progress?.Report(status);
        }

        return results;
    }

    public async Task<OptimizationStatus> AssessAsync(string optimizationId, CancellationToken cancellationToken = default)
    {
        IOptimization optimization = GetRequired(optimizationId);
        return await BuildStatusAsync(optimization, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChangeSet?> GetBackupAsync(string optimizationId, CancellationToken cancellationToken = default)
    {
        _ = GetRequired(optimizationId);
        return await _backups.TryLoadAsync(optimizationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationOutcome> ApplyAsync(string optimizationId, CancellationToken cancellationToken = default)
    {
        IOptimization optimization = GetRequired(optimizationId);
        SemaphoreSlim gate = _gates.GetOrAdd(optimizationId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            OptimizationAssessment before = await AssessSafeAsync(optimization, cancellationToken).ConfigureAwait(false);
            switch (before.State)
            {
                case OptimizationState.Applied:
                    return OperationOutcome.Ok($"{optimization.Descriptor.Name} is already applied.", verified: true);
                case OptimizationState.Unsupported:
                case OptimizationState.ReviewRequired:
                    return OperationOutcome.Failed(
                        before.BlockedReason ?? $"{optimization.Descriptor.Name} cannot be applied on this PC.");
                case OptimizationState.Unknown:
                    return OperationOutcome.Failed(
                        before.BlockedReason ?? "The current state could not be determined, so nothing was changed.");
            }

            ChangeSet original;
            try
            {
                original = await optimization.CaptureAsync(_context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return OperationOutcome.Failed($"Could not capture the original state; nothing was changed: {ex.Message}");
            }

            if (original.Entries.Count == 0 && original.Metadata.Count == 0)
            {
                return OperationOutcome.Failed(
                    "The module did not capture any restorable state, so the change was refused.");
            }

            if (!string.Equals(original.OptimizationId, optimization.Descriptor.Id, StringComparison.Ordinal))
            {
                return OperationOutcome.Failed(
                    $"The captured backup belongs to '{original.OptimizationId}' instead of '{optimization.Descriptor.Id}'.");
            }

            try
            {
                await _backups.SaveAsync(original, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return OperationOutcome.Failed($"The backup could not be saved; nothing was changed: {ex.Message}");
            }

            bool useElevation = optimization.Descriptor.RequiresElevation && !_context.IsElevated;
            OperationOutcome outcome = useElevation
                ? await ApplyElevatedAsync(optimization, original, cancellationToken).ConfigureAwait(false)
                : await ApplyInProcessAsync(optimization, original, cancellationToken).ConfigureAwait(false);

            if (!outcome.Success)
            {
                await TryLogAsync(optimization, "apply", outcome.RolledBack ? "rolled-back" : "failed", outcome, cancellationToken)
                    .ConfigureAwait(false);
                return outcome;
            }

            OptimizationAssessment after = await AssessSafeAsync(optimization, cancellationToken).ConfigureAwait(false);
            bool verified = after.State == OptimizationState.Applied;
            var warnings = new List<string>(outcome.Warnings);
            if (!verified)
            {
                warnings.Add(
                    $"The settings were written but the expected state could not be verified ({after.Headline}). " +
                    "Review the details, or use Undo to restore the backup.");
            }

            outcome = outcome with { Verified = verified, Warnings = warnings };
            await TryLogAsync(optimization, "apply", verified ? "verified" : "applied-unverified", outcome, cancellationToken)
                .ConfigureAwait(false);
            return outcome;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<OperationOutcome> UndoAsync(string optimizationId, CancellationToken cancellationToken = default)
    {
        IOptimization optimization = GetRequired(optimizationId);
        SemaphoreSlim gate = _gates.GetOrAdd(optimizationId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ChangeSet? backup;
            try
            {
                backup = await _backups.TryLoadAsync(optimizationId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is BackupStoreException or IOException or UnauthorizedAccessException)
            {
                return OperationOutcome.Failed($"The backup could not be read: {ex.Message}");
            }

            if (backup is null)
            {
                return OperationOutcome.Failed(
                    "No backup exists for this optimization. Settings changed outside the toolkit cannot be undone automatically.");
            }

            if (!RestoreScopeValidator.IsValid(optimization.Descriptor, backup, out string scopeError))
            {
                return OperationOutcome.Failed($"Refusing to restore settings that fail validation: {scopeError}");
            }

            bool useElevation = optimization.Descriptor.RequiresElevation && !_context.IsElevated;
            OperationOutcome outcome = useElevation
                ? await RestoreElevatedAsync(optimization, backup, cancellationToken).ConfigureAwait(false)
                : await RestoreInProcessAsync(optimization, backup, cancellationToken).ConfigureAwait(false);

            if (outcome.Success)
            {
                bool verified;
                List<string> mismatches;
                if (backup.Entries.Count > 0)
                {
                    verified = ChangeSetInspector.Matches(_context.Registry, backup, out mismatches);
                    if (!verified)
                    {
                        outcome = outcome with
                        {
                            Warnings = [.. outcome.Warnings, .. mismatches.Select(m => "Still different: " + m)],
                        };
                    }
                }
                else
                {
                    verified = outcome.Verified;
                }

                outcome = outcome with { Verified = verified };
                await TryLogAsync(optimization, "restore", verified ? "verified" : "restored-unverified", outcome, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await TryLogAsync(optimization, "restore", "failed", outcome, cancellationToken).ConfigureAwait(false);
            }

            return outcome;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<OptimizationStatus> BuildStatusAsync(IOptimization optimization, CancellationToken cancellationToken)
    {
        OptimizationAssessment assessment = await AssessSafeAsync(optimization, cancellationToken).ConfigureAwait(false);
        ChangeSet? backup = null;
        try
        {
            backup = await _backups.TryLoadAsync(optimization.Descriptor.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is BackupStoreException or IOException or UnauthorizedAccessException)
        {
            // A damaged backup should not hide the card; the details view reports the problem on undo.
        }

        return new OptimizationStatus(optimization, assessment, backup);
    }

    private async Task<OperationOutcome> ApplyInProcessAsync(
        IOptimization optimization,
        ChangeSet original,
        CancellationToken cancellationToken)
    {
        OperationOutcome outcome;
        try
        {
            outcome = await optimization.ApplyAsync(_context, original, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            outcome = OperationOutcome.Failed($"Apply failed: {ex.Message}");
        }

        if (outcome.Success)
        {
            return outcome;
        }

        // A partially applied optimization must never be left in an unknown state.
        var warnings = new List<string>(outcome.Warnings)
        {
            "The change did not complete, so the captured original state was restored.",
        };

        try
        {
            OperationOutcome rollback = await optimization.RestoreAsync(_context, original, cancellationToken).ConfigureAwait(false);
            bool matched = original.Entries.Count == 0 ||
                           ChangeSetInspector.Matches(_context.Registry, original, out _);
            if (!rollback.Success || !matched)
            {
                warnings.Add("The automatic rollback could not be fully verified. Use Undo to retry restoring the backup.");
                return outcome with { Warnings = warnings };
            }

            return outcome with { RolledBack = true, Warnings = warnings };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add($"The automatic rollback failed: {ex.Message}. The backup is preserved; use Undo to retry.");
            return outcome with { Warnings = warnings };
        }
    }

    private async Task<OperationOutcome> RestoreInProcessAsync(
        IOptimization optimization,
        ChangeSet backup,
        CancellationToken cancellationToken)
    {
        try
        {
            return await optimization.RestoreAsync(_context, backup, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OperationOutcome.Failed($"Undo failed: {ex.Message}");
        }
    }

    private async Task<OperationOutcome> ApplyElevatedAsync(
        IOptimization optimization,
        ChangeSet original,
        CancellationToken cancellationToken)
    {
        if (_elevation is null)
        {
            return OperationOutcome.Failed(
                "This change requires administrator rights, but the elevated helper is unavailable in this build.");
        }

        try
        {
            ElevatedOperationRequest request =
                ElevatedOperationRequest.Create(optimization.Descriptor.Id, ElevatedOperationKind.Apply, original);
            ElevatedOperationResponse response = await _elevation.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            return response.ToOutcome();
        }
        catch (ElevationCancelledException)
        {
            return OperationOutcome.Failed("Administrator approval was cancelled. Nothing was changed.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OperationOutcome.Failed($"The elevated helper could not complete the change: {ex.Message}");
        }
    }

    private async Task<OperationOutcome> RestoreElevatedAsync(
        IOptimization optimization,
        ChangeSet backup,
        CancellationToken cancellationToken)
    {
        if (_elevation is null)
        {
            return OperationOutcome.Failed(
                "This change requires administrator rights, but the elevated helper is unavailable in this build.");
        }

        try
        {
            ElevatedOperationRequest request =
                ElevatedOperationRequest.Create(optimization.Descriptor.Id, ElevatedOperationKind.Restore, backup);
            ElevatedOperationResponse response = await _elevation.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            return response.ToOutcome();
        }
        catch (ElevationCancelledException)
        {
            return OperationOutcome.Failed("Administrator approval was cancelled. Nothing was changed.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OperationOutcome.Failed($"The elevated helper could not complete the undo: {ex.Message}");
        }
    }

    private async Task<OptimizationAssessment> AssessSafeAsync(
        IOptimization optimization,
        CancellationToken cancellationToken)
    {
        try
        {
            return await optimization.AssessAsync(_context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OptimizationAssessment.Unknown($"Inspection failed: {ex.Message}");
        }
    }

    private async Task TryLogAsync(
        IOptimization optimization,
        string action,
        string result,
        OperationOutcome outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            await _log.AppendAsync(
                new ActionLogEntry
                {
                    OptimizationId = optimization.Descriptor.Id,
                    OptimizationName = optimization.Descriptor.Name,
                    Action = action,
                    Result = result,
                    Message = outcome.Message,
                    Changes = [.. outcome.Changes],
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Local history is best effort; never fail a completed system change because logging failed.
        }
    }

    private IOptimization GetRequired(string optimizationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(optimizationId);
        return Find(optimizationId)
               ?? throw new ArgumentException($"Unknown optimization id '{optimizationId}'.", nameof(optimizationId));
    }
}
