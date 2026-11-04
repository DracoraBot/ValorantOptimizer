using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Read-only storage overview. It reports system-drive free/total space and a bounded estimate of
/// the temp-folder size, but never deletes files or probes SMART/drive health.
/// </summary>
public sealed class StorageOverviewReview : IOptimization
{
    public const string OptimizationId = "storage-overview";

    private const string BlockedReason =
        "This card is read-only: it never deletes temp or cached files, never cleans the drive and " +
        "does not probe SMART/drive health. Free-space reporting is informational only; clean up files " +
        "deliberately or use Windows Storage Sense if you choose to.";

    private readonly IStorageOverviewProbe _probe;
    private readonly string? _systemDriveRoot;
    private readonly string? _tempDirectoryPath;

    /// <summary>Creates the module with the real file-system probe and default paths.</summary>
    public StorageOverviewReview()
        : this(new SystemStorageOverviewProbe())
    {
    }

    /// <summary>Creates the module with an injected probe and optional path roots for tests.</summary>
    public StorageOverviewReview(
        IStorageOverviewProbe probe,
        string? systemDriveRoot = null,
        string? tempDirectoryPath = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _systemDriveRoot = string.IsNullOrWhiteSpace(systemDriveRoot) ? null : systemDriveRoot;
        _tempDirectoryPath = string.IsNullOrWhiteSpace(tempDirectoryPath) ? null : tempDirectoryPath;
    }

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = OptimizationId,
        Name = "Storage overview (review)",
        Category = OptimizationCategory.Storage,
        Risk = RiskLevel.Safe,
        Summary = "Show system-drive free space and an estimate of the temp folder's size without deleting anything.",
        WhatItChanges =
            "Nothing. Reads DriveInfo totals/free space for the system drive and measures the temp " +
            "folder with a bounded directory enumeration. No file is deleted, moved, created or compressed.",
        Why =
            "Low free space can affect Windows updates, shader caches and page-file growth, but deleting " +
            "files automatically is a destructive decision that depends on what is safe to remove. The " +
            "card reports the numbers and leaves cleanup to you. SMART health and drive-failure " +
            "prediction are deliberately out of scope; use the drive vendor's tool for those. No " +
            "frame-rate change is promised.",
        Compatibility =
            "Windows 10 and Windows 11. Drive figures come from DriveInfo for the system drive; the temp " +
            "estimate is bounded and may skip protected or very large folder trees.",
        StateWhenApplied = "Read-only review",
        Restart = RestartRequirement.None,
        RequiresElevation = false,
        ManagedScopes = [],
        Notes =
        [
            "Read-only: apply and restore are refused by design, and the card is disabled in the UI.",
            "Temp and cache files are never deleted by this card.",
            "SMART health and drive failure prediction are not probed; use the drive vendor's tool.",
            "The temp-folder size is a bounded estimate and can be smaller than what Explorer reports.",
        ],
    };

    public Task<OptimizationAssessment> AssessAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        string driveRoot = SystemDriveRoot();
        string tempPath = TempDirectoryPath();
        SystemDriveSpace? drive = ReadDrive(driveRoot);
        long? tempBytes = MeasureDirectory(tempPath);

        var evidence = new List<AssessmentEvidence>();
        if (drive is null)
        {
            evidence.Add(new AssessmentEvidence("System drive", $"Unavailable for {driveRoot}"));
        }
        else
        {
            evidence.Add(new AssessmentEvidence(
                "System drive",
                $"{ReviewOnlySupport.FormatBytes(drive.FreeBytes)} free of {ReviewOnlySupport.FormatBytes(drive.TotalBytes)} " +
                $"({drive.FreePercent}% free)"));
            evidence.Add(new AssessmentEvidence(
                "Drive usage",
                $"{ReviewOnlySupport.FormatBytes(drive.UsedBytes)} used on {drive.RootPath}"));
        }

        evidence.Add(new AssessmentEvidence(
            "Temp folder",
            tempBytes is long size
                ? $"{ReviewOnlySupport.FormatBytes(size)} in {tempPath}"
                : $"Unavailable ({tempPath})"));
        evidence.Add(new AssessmentEvidence(
            "SMART health",
            "Not probed (read-only card)"));

        string headline = drive is null
            ? "Storage overview: system drive unavailable"
            : $"Storage overview: {ReviewOnlySupport.FormatBytes(drive.FreeBytes)} free of {ReviewOnlySupport.FormatBytes(drive.TotalBytes)}";

        OptimizationAssessment assessment = OptimizationAssessment.Review(BlockedReason, headline) with
        {
            Explanation =
                "The drive and temp-folder numbers above are shown for context only. Windows and " +
                "applications manage temp data; deleting the wrong files can break installs, so this " +
                "read-only card leaves cleanup to you. Nothing is changed by this card.",
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

        string driveRoot = SystemDriveRoot();
        string tempPath = TempDirectoryPath();
        SystemDriveSpace? drive = ReadDrive(driveRoot);
        long? tempBytes = MeasureDirectory(tempPath);

        ChangeSet changeSet = ReviewOnlySupport.CreateChangeSet(
            Descriptor.Id,
            ("systemDriveRoot", driveRoot),
            ("systemDriveTotalBytes", drive is null ? "unavailable" : ReviewOnlySupport.Invariant(drive.TotalBytes)),
            ("systemDriveFreeBytes", drive is null ? "unavailable" : ReviewOnlySupport.Invariant(drive.FreeBytes)),
            ("tempDirectoryPath", tempPath),
            ("tempDirectoryBytes", tempBytes is long size ? ReviewOnlySupport.Invariant(size) : "unavailable"));

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

    /// <summary>Formats a byte count the same way the UI evidence does (binary units, invariant).</summary>
    public static string FormatBytes(long bytes) => ReviewOnlySupport.FormatBytes(bytes);

    private string SystemDriveRoot() => _systemDriveRoot ?? DefaultSystemDriveRoot();

    private string TempDirectoryPath() => _tempDirectoryPath ?? Path.GetTempPath();

    private SystemDriveSpace? ReadDrive(string rootPath)
    {
        try
        {
            return _probe.ReadDrive(rootPath);
        }
        catch (Exception ex) when (IsExpectedProbeFailure(ex))
        {
            return null;
        }
    }

    private long? MeasureDirectory(string directoryPath)
    {
        try
        {
            return _probe.MeasureDirectoryBytes(directoryPath);
        }
        catch (Exception ex) when (IsExpectedProbeFailure(ex))
        {
            return null;
        }
    }

    private static string DefaultSystemDriveRoot()
    {
        string? root = Path.GetPathRoot(Environment.SystemDirectory);
        if (!string.IsNullOrWhiteSpace(root))
        {
            return root;
        }

        root = Path.GetPathRoot(Environment.CurrentDirectory);
        return string.IsNullOrWhiteSpace(root) ? "C:\\" : root;
    }

    private static bool IsExpectedProbeFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or InvalidOperationException
            or System.Security.SecurityException;
}
