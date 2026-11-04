using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Read-only memory overview. It reports total/used physical memory and the largest background
/// working sets so the player can see pressure around VALORANT, but it never runs a "RAM cleaner",
/// trims working sets or stops services.
/// </summary>
public sealed class MemoryOverviewReview : IOptimization
{
    public const string OptimizationId = "memory-overview";

    private const string BlockedReason =
        "This card is read-only: no automatic RAM \"cleaner\" runs, and memory compression/background " +
        "services are monitored only. Free RAM is not a reliable performance target on modern Windows, " +
        "so the toolkit does not trim working sets or stop processes for you.";

    private readonly IMemoryOverviewProbe _probe;

    /// <summary>Creates the module with the real Win32 probe.</summary>
    public MemoryOverviewReview()
        : this(new Win32MemoryOverviewProbe())
    {
    }

    /// <summary>Creates the module with an injected probe (tests pass a deterministic fake).</summary>
    public MemoryOverviewReview(IMemoryOverviewProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = OptimizationId,
        Name = "Memory overview (review)",
        Category = OptimizationCategory.Memory,
        Risk = RiskLevel.Safe,
        Summary = "Show total and used RAM plus the largest background processes so memory pressure is visible; no RAM cleaner runs.",
        WhatItChanges =
            "Nothing. Reads total/available physical memory and up to three process working sets for " +
            "display only. No process is suspended or stopped, no working set is trimmed and no memory " +
            "is freed by this card.",
        Why =
            "Free RAM is not a reliable performance target: Windows caches file data aggressively and " +
            "memory compression already reclaims pages, so a high \"used\" number is not automatically a " +
            "problem. The overview gives context for deciding whether background apps are competing " +
            "with VALORANT; it intentionally avoids RAM-cleaner behavior because trimming working sets " +
            "usually forces re-reads and can hurt the game. No frame-rate change is promised.",
        Compatibility =
            "Windows 10 and Windows 11. Total memory comes from GlobalMemoryStatusEx; process working " +
            "sets may be unavailable for protected processes, and the list is intentionally capped at " +
            "three entries.",
        StateWhenApplied = "Read-only review",
        Restart = RestartRequirement.None,
        RequiresElevation = false,
        ManagedScopes = [],
        Notes =
        [
            "Read-only: apply and restore are refused by design, and the card is disabled in the UI.",
            "No RAM cleaner, working-set trimmer or service stop is ever performed by this card.",
            "Memory compression and background services are monitored only for context.",
            "Windows caches file data in RAM by design; a high used percentage does not necessarily mean low performance.",
        ],
    };

    public Task<OptimizationAssessment> AssessAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        MemoryOverviewSnapshot? snapshot = ReadSnapshot();

        if (snapshot is null)
        {
            OptimizationAssessment unavailable = OptimizationAssessment.Review(
                BlockedReason,
                "Memory overview unavailable") with
            {
                Explanation =
                    "Physical memory could not be read on this attempt, so the card does not guess at RAM " +
                    "usage or process sizes. Nothing is changed by this card.",
                Evidence =
                [
                    new AssessmentEvidence("RAM total", "Unavailable"),
                    new AssessmentEvidence("RAM used", "Unavailable"),
                    new AssessmentEvidence("Largest background processes", "Unavailable"),
                ],
            };
            return Task.FromResult(unavailable);
        }

        ulong total = snapshot.TotalPhysicalBytes;
        ulong used = snapshot.UsedPhysicalBytes;
        int usedPercent = total == 0
            ? 0
            : (int)Math.Round(used * 100d / total, MidpointRounding.AwayFromZero);

        var evidence = new List<AssessmentEvidence>
        {
            new("RAM total", ReviewOnlySupport.FormatBytes(total)),
            new("RAM used", $"{ReviewOnlySupport.FormatBytes(used)} ({usedPercent}% of total)"),
        };

        IReadOnlyList<MemoryConsumer> consumers = snapshot.LargestBackgroundProcesses
            .OrderByDescending(consumer => consumer.WorkingSetBytes)
            .ThenBy(consumer => consumer.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (MemoryConsumer consumer in consumers.Take(Win32MemoryOverviewProbe.MaximumBackgroundProcesses))
        {
            evidence.Add(new AssessmentEvidence(
                $"Background process: {consumer.Name}",
                ReviewOnlySupport.FormatBytes(consumer.WorkingSetBytes)));
        }

        if (consumers.Count == 0)
        {
            evidence.Add(new AssessmentEvidence(
                "Largest background processes",
                "No process working sets could be read"));
        }

        string headline = total == 0
            ? "Memory overview is available but total RAM is unknown"
            : $"Memory overview: {ReviewOnlySupport.FormatBytes(used)} in use of {ReviewOnlySupport.FormatBytes(total)}";

        OptimizationAssessment assessment = OptimizationAssessment.Review(BlockedReason, headline) with
        {
            Explanation =
                "Windows is already managing cached and compressed memory; the numbers above are shown " +
                "for context only. If VALORANT is struggling, close background applications deliberately " +
                "or add physical RAM rather than relying on a RAM cleaner. Nothing is changed by this card.",
            Evidence = evidence,
        };

        return Task.FromResult(assessment);
    }

    public Task<ChangeSet> CaptureAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        MemoryOverviewSnapshot? snapshot = ReadSnapshot();
        if (snapshot is null)
        {
            ChangeSet unavailable = ReviewOnlySupport.CreateChangeSet(
                Descriptor.Id,
                ("memoryOverview", "unavailable"));
            return Task.FromResult(unavailable);
        }

        string processes = snapshot.LargestBackgroundProcesses.Count == 0
            ? "none"
            : string.Join(
                " | ",
                snapshot.LargestBackgroundProcesses
                    .OrderByDescending(consumer => consumer.WorkingSetBytes)
                    .ThenBy(consumer => consumer.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(Win32MemoryOverviewProbe.MaximumBackgroundProcesses)
                    .Select(consumer => $"{consumer.Name}={ReviewOnlySupport.Invariant(consumer.WorkingSetBytes)}"));

        ChangeSet changeSet = ReviewOnlySupport.CreateChangeSet(
            Descriptor.Id,
            ("totalPhysicalBytes", ReviewOnlySupport.Invariant(snapshot.TotalPhysicalBytes)),
            ("availablePhysicalBytes", ReviewOnlySupport.Invariant(snapshot.AvailablePhysicalBytes)),
            ("usedPhysicalBytes", ReviewOnlySupport.Invariant(snapshot.UsedPhysicalBytes)),
            ("largestBackgroundProcesses", processes));

        return Task.FromResult(changeSet);
    }

    public Task<OperationOutcome> ApplyAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default) =>
        ReviewOnlySupport.RefuseAsync(context, original, cancellationToken);

    public Task<OperationOutcome> RestoreAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default) =>
        ReviewOnlySupport.RefuseAsync(context, original, cancellationToken);

    private MemoryOverviewSnapshot? ReadSnapshot()
    {
        try
        {
            return _probe.Read();
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Formats a byte count the same way the UI evidence does (binary units, invariant).</summary>
    public static string FormatBytes(ulong bytes) => ReviewOnlySupport.FormatBytes(bytes);
}
