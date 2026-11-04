using ValorantOptimizer.Core;
using ValorantOptimizer.Diagnostics;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Read-only review of the VALORANT installation and user config folder. In-game graphics,
/// raw-input and sensitivity settings belong to VALORANT, so this card only reports context and
/// never edits game files or registry values.
/// </summary>
public sealed class ValorantConfigReview : IOptimization
{
    public const string OptimizationId = "valorant-config";

    private const string BlockedReason =
        "In-game graphics, raw input and sensitivity settings are owned by VALORANT and are reviewed " +
        "inside the game. This read-only card never edits VALORANT configuration files, the game's " +
        "saved config or any related registry value.";

    private readonly IValorantLocator _locator;
    private readonly string? _configRootOverride;

    /// <summary>Creates the review card for the real locator and the user's LOCALAPPDATA folder.</summary>
    public ValorantConfigReview(IValorantLocator locator)
        : this(locator, configRootPath: null)
    {
    }

    /// <summary>Creates the review card with an optional config-root override (tests use a temp path).</summary>
    public ValorantConfigReview(IValorantLocator locator, string? configRootPath)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _configRootOverride = string.IsNullOrWhiteSpace(configRootPath) ? null : configRootPath;
    }

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = OptimizationId,
        Name = "VALORANT config (review)",
        Category = OptimizationCategory.Valorant,
        Risk = RiskLevel.Safe,
        Summary = "Confirm the VALORANT install, version and user config folder; in-game graphics and raw-input settings stay in the game.",
        WhatItChanges =
            @"Nothing. Uses IValorantLocator to report the install path and version, and checks whether " +
            @"%LOCALAPPDATA%\VALORANT\Saved\Config exists. No game file, config file, registry value or " +
            "in-game setting is changed.",
        Why =
            "Graphics quality, resolution, raw input buffer, sensitivity and other competitive settings are " +
            "best reviewed in VALORANT's own settings menu, where they apply to the game's account and " +
            "saved configuration. The toolkit intentionally does not edit those files: doing so can be " +
            "overwritten by the game, break per-account settings or conflict with Riot's support model. " +
            "This card shows where the install and config folder are. No frame-rate change is promised.",
        Compatibility =
            "Windows 10 and Windows 11 with a VALORANT installation the locator can find. The user config " +
            "folder may be absent before the first launch, and Riot's installation metadata is the source " +
            "of the reported path.",
        StateWhenApplied = "Read-only review",
        Restart = RestartRequirement.None,
        RequiresElevation = false,
        ManagedScopes = [],
        Notes =
        [
            "Read-only: apply and restore are refused by design, and the card is disabled in the UI.",
            "No VALORANT config file, registry value or game setting is edited by this card.",
            "The locator result depends on the machine; re-run the scan if VALORANT was moved.",
            @"%LOCALAPPDATA%\VALORANT\Saved\Config may be missing before the first game launch.",
        ],
    };

    public async Task<OptimizationAssessment> AssessAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        ValorantInstallation? installation = await LocateAsync(cancellationToken).ConfigureAwait(false);
        string configRoot = ConfigRoot();
        ConfigFolderState config = InspectConfigFolder(configRoot);

        var evidence = new List<AssessmentEvidence>
        {
            new("Install location", DescribeInstallLocation(installation)),
            new("Version", DescribeVersion(installation)),
        };

        if (installation?.DetectionSource is { Length: > 0 } source)
        {
            evidence.Add(new AssessmentEvidence("Detection source", source));
        }

        evidence.Add(new AssessmentEvidence(
            "User config folder",
            config.Exists
                ? $"Present: {configRoot}"
                : config.Error is not null
                    ? $"Could not inspect: {configRoot}"
                    : $"Not found: {configRoot}"));

        evidence.Add(new AssessmentEvidence(
            "In-game settings",
            "Reviewed in VALORANT (graphics, raw input, sensitivity)"));

        bool found = installation?.IsFound == true;
        string headline = found
            ? $"VALORANT detected; user config folder {(config.Exists ? "found" : "not found")}"
            : "VALORANT configuration review";

        string explanation = found
            ? "The locator found VALORANT and the context above is reported for review. " +
              "Adjust graphics, resolution, raw input buffer and sensitivity in VALORANT's settings menu; " +
              "this read-only card never edits those files. Nothing is changed by this card."
            : "VALORANT was not detected on this machine, so the install evidence is unavailable. " +
              "In-game graphics, raw input and sensitivity settings are still reviewed inside the game " +
              "itself; this read-only card never edits them. Nothing is changed by this card.";

        OptimizationAssessment assessment = OptimizationAssessment.Review(BlockedReason, headline) with
        {
            Explanation = explanation,
            Evidence = evidence,
        };

        return assessment;
    }

    public async Task<ChangeSet> CaptureAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        ValorantInstallation? installation = await LocateAsync(cancellationToken).ConfigureAwait(false);
        string configRoot = ConfigRoot();
        ConfigFolderState config = InspectConfigFolder(configRoot);

        ChangeSet changeSet = ReviewOnlySupport.CreateChangeSet(
            Descriptor.Id,
            ("installDirectory", installation?.InstallDirectory ?? "not detected"),
            ("executablePath", installation?.ExecutablePath ?? "not detected"),
            ("version", installation?.Version ?? "unknown"),
            ("detectionSource", installation?.DetectionSource ?? "unknown"),
            ("configRootPath", configRoot),
            ("configRootExists", config.Exists ? "true" : "false"));

        return changeSet;
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

    private async Task<ValorantInstallation?> LocateAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private string ConfigRoot()
    {
        if (_configRootOverride is not null)
        {
            return _configRootOverride;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            localAppData = string.IsNullOrWhiteSpace(profile)
                ? string.Empty
                : Path.Combine(profile, "AppData", "Local");
        }

        return Path.Combine(localAppData, "VALORANT", "Saved", "Config");
    }

    private static ConfigFolderState InspectConfigFolder(string path)
    {
        try
        {
            return new ConfigFolderState(Directory.Exists(path), Error: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new ConfigFolderState(Exists: false, ex.Message);
        }
    }

    private static string DescribeInstallLocation(ValorantInstallation? installation)
    {
        if (installation is null)
        {
            return "Not detected (locator unavailable)";
        }

        if (!string.IsNullOrWhiteSpace(installation.InstallDirectory))
        {
            return installation.InstallDirectory;
        }

        return string.IsNullOrWhiteSpace(installation.ExecutablePath)
            ? "Not detected"
            : installation.ExecutablePath;
    }

    private static string DescribeVersion(ValorantInstallation? installation) =>
        string.IsNullOrWhiteSpace(installation?.Version) ? "Unknown" : installation.Version;

    private sealed record ConfigFolderState(bool Exists, string? Error);
}
