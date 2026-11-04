using System.Globalization;
using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Diagnostics;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Adds the <c>DISABLEDXMAXIMIZEDWINDOWEDMODE</c> per-application compatibility flag to the
/// VALORANT launcher and game binaries. The flag disables Windows' fullscreen-optimization
/// (maximized windowed) path for those executables. Existing compatibility tokens are preserved.
/// </summary>
public sealed class ValorantAppCompatOptimization : IOptimization
{
    public const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const string LauncherFileName = "VALORANT.exe";
    private const string ShippingFileName = "VALORANT-Win64-Shipping.exe";

    private readonly IValorantLocator _locator;

    public ValorantAppCompatOptimization(IValorantLocator locator)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = "valorant-appcompat",
        Name = "VALORANT fullscreen-optimization compatibility flag",
        Category = OptimizationCategory.Valorant,
        Risk = RiskLevel.Safe,
        Summary = "Adds the DISABLEDXMAXIMIZEDWINDOWEDMODE compatibility flag to VALORANT so Windows does not apply its fullscreen-optimization window path to the game.",
        WhatItChanges = @"Writes or updates the per-executable REG_SZ values under HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers for VALORANT.exe and, when located, VALORANT-Win64-Shipping.exe. Existing compatibility tokens are preserved; the module only appends DISABLEDXMAXIMIZEDWINDOWEDMODE and ensures the leading '~' marker. Undo restores the exact previous data or deletes values that did not exist.",
        Why = "Windows' application-compatibility layer can apply fullscreen optimizations to a game window. DISABLEDXMAXIMIZEDWINDOWEDMODE is the compatibility flag that opts one executable out of the maximized-windowed/fullscreen-optimization path, which some players use to avoid Windows interfering with the game's chosen display mode. The effect depends on the game's video settings, GPU driver and Windows version, and no frame-rate gain is promised.",
        Compatibility = "Windows 10 and Windows 11. Per-user setting; no administrator rights are required. The flag is read when an executable starts, so restart the launcher/game. A VALORANT update or Windows servicing can reset AppCompat entries; reassess and re-apply if needed.",
        StateWhenApplied = "Windowed-mode flag on",
        Restart = RestartRequirement.None,
        RequiresElevation = false,
        ManagedScopes = [new RegistryScope(RegistryHive.CurrentUser, LayersKey)],
        Notes =
        [
            "Targets both VALORANT.exe (launcher) and VALORANT-Win64-Shipping.exe (game binary) when the locator finds them; if only the launcher is found, the game process may not receive the flag.",
            "Existing compatibility tokens for the executable are preserved; only the flag is appended and the '~' marker is added if missing.",
            "This does not change in-game video settings such as fullscreen vs. borderless; check those separately in VALORANT's settings.",
            "Windows registers AppCompat layers per user. Undo restores the exact previous data or deletes a value that did not exist before.",
        ],
    };

    public async Task<OptimizationAssessment> AssessAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<string> targets = await ResolveTargetsAsync(cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            return new OptimizationAssessment
            {
                State = OptimizationState.Unsupported,
                Headline = "VALORANT installation not found",
                BlockedReason =
                    "No VALORANT executable was located, so the fullscreen-optimization compatibility flag " +
                    "cannot be targeted. Install VALORANT (or re-run the scan) and try again.",
            };
        }

        var evidence = new List<AssessmentEvidence>();
        bool allApplied = true;
        foreach (string target in targets)
        {
            CurrentLayer current = ReadLayer(context.Registry, target);
            allApplied &= current.HasFlag;
            evidence.Add(new AssessmentEvidence(Path.GetFileName(target), Describe(current)));
        }

        bool launcherFound = targets.Any(target => IsExecutable(target, LauncherFileName));
        bool shippingFound = targets.Any(target => IsExecutable(target, ShippingFileName));

        if (!launcherFound)
        {
            evidence.Add(new AssessmentEvidence(
                LauncherFileName,
                "Not found; only the game binary will be configured."));
        }

        if (!shippingFound)
        {
            evidence.Add(new AssessmentEvidence(
                ShippingFileName,
                "Not found; only the launcher will be configured, so the game process may not receive the flag."));
        }

        string explanation = allApplied
            ? "The DISABLEDXMAXIMIZEDWINDOWEDMODE compatibility flag is present for the configured VALORANT executables. " +
              "It takes effect when each executable next starts."
            : "Applying appends the DISABLEDXMAXIMIZEDWINDOWEDMODE flag to the per-user compatibility entries and preserves any " +
              "existing tokens. It takes effect when each executable next starts; no reboot is required.";

        if (!shippingFound)
        {
            explanation += " The game binary (VALORANT-Win64-Shipping.exe) was not located; only the launcher will be configured.";
        }

        if (!launcherFound)
        {
            explanation += " The launcher (VALORANT.exe) was not located; only the game binary will be configured.";
        }

        return new OptimizationAssessment
        {
            State = allApplied ? OptimizationState.Applied : OptimizationState.NotApplied,
            Headline = allApplied
                ? "Fullscreen-optimization compatibility flag is set for VALORANT"
                : "Fullscreen-optimization compatibility flag is not set for VALORANT",
            Explanation = explanation,
            Evidence = evidence,
        };
    }

    public async Task<ChangeSet> CaptureAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<string> targets = await ResolveTargetsAsync(cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            throw new InvalidOperationException(
                "No VALORANT executable was located, so there is no compatibility-flag state to capture.");
        }

        List<RegistrySnapshotEntry> entries = targets
            .Select(target => RegistrySnapshot.Capture(
                context.Registry,
                RegistryHive.CurrentUser,
                LayersKey,
                target))
            .ToList();

        return new ChangeSet
        {
            OptimizationId = Descriptor.Id,
            Entries = entries,
        };
    }

    public Task<OperationOutcome> ApplyAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(original);
        cancellationToken.ThrowIfCancellationRequested();

        if (original.Entries.Count == 0)
        {
            return Task.FromResult(OperationOutcome.Failed(
                "No captured VALORANT compatibility targets were supplied, so nothing was changed."));
        }

        if (original.Entries.Any(entry => !IsManagedEntry(entry)))
        {
            return Task.FromResult(OperationOutcome.Failed(
                "The captured state contains entries outside this optimization's registry scope, " +
                "so nothing was changed."));
        }

        var changes = new List<string>();
        foreach (RegistrySnapshotEntry entry in original.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CurrentLayer current = ReadLayer(context.Registry, entry.ValueName);
            string existing = current.IsString ? current.Text ?? string.Empty : string.Empty;

            if (current.IsString && current.HasFlag)
            {
                changes.Add($"{entry.ValueName} already had {AppCompatLayerData.Flag}.");
                continue;
            }

            string merged = AppCompatLayerData.Merge(existing);
            context.Registry.WriteValue(
                RegistryHive.CurrentUser,
                LayersKey,
                entry.ValueName,
                RegistryValueKind.String,
                merged);
            changes.Add($"Set {entry.ValueName} to '{merged}'.");
        }

        return Task.FromResult(new OperationOutcome
        {
            Success = true,
            Message =
                $"Added the {AppCompatLayerData.Flag} compatibility flag for {original.Entries.Count} " +
                "VALORANT executable(s). It takes effect the next time each executable starts; no reboot is required.",
            Changes = changes,
            Warnings = MissingExecutableWarnings(original.Entries),
        });
    }

    public Task<OperationOutcome> RestoreAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(original);
        cancellationToken.ThrowIfCancellationRequested();

        if (original.Entries.Count == 0)
        {
            return Task.FromResult(OperationOutcome.Failed(
                "The backup does not contain any VALORANT compatibility-flag entries to restore."));
        }

        if (original.Entries.Any(entry => !IsManagedEntry(entry)))
        {
            return Task.FromResult(OperationOutcome.Failed(
                "The backup contains registry entries outside this optimization's scope, " +
                "so it was not restored."));
        }

        RegistrySnapshot.RestoreAll(context.Registry, original.Entries);

        return Task.FromResult(OperationOutcome.Ok(
            $"Restored {original.Entries.Count} VALORANT compatibility entr" +
            $"{(original.Entries.Count == 1 ? "y" : "ies")} to the exact state captured before the change.") with
        {
            Changes = original.Entries.Select(entry => entry.ToString()).ToList(),
        });
    }

    private static bool IsManagedEntry(RegistrySnapshotEntry entry) =>
        entry.Hive == RegistryHive.CurrentUser &&
        string.Equals(entry.SubKey, LayersKey, StringComparison.OrdinalIgnoreCase);

    private static CurrentLayer ReadLayer(IRegistryStore registry, string executablePath)
    {
        RegistryValue? value = registry.ReadValue(RegistryHive.CurrentUser, LayersKey, executablePath);
        if (value is null)
        {
            return default;
        }

        bool isString = value.Kind is RegistryValueKind.String or RegistryValueKind.ExpandString;
        string? text = isString ? Convert.ToString(value.Data, CultureInfo.InvariantCulture) : null;
        return new CurrentLayer(
            Exists: true,
            IsString: isString,
            Text: text,
            HasFlag: isString && AppCompatLayerData.ContainsFlag(text),
            Kind: value.Kind);
    }

    private static string Describe(CurrentLayer current)
    {
        if (!current.Exists)
        {
            return "Not set (no compatibility flags).";
        }

        if (!current.IsString)
        {
            return $"Stored as {current.Kind} instead of REG_SZ; applying replaces it with '{AppCompatLayerData.DefaultData}' and Undo restores the original kind and data.";
        }

        if (current.HasFlag)
        {
            return $"Flag present ('{Truncate(current.Text, 100)}').";
        }

        return string.IsNullOrWhiteSpace(current.Text)
            ? $"Value exists but is empty; applying writes '{AppCompatLayerData.DefaultData}'."
            : $"Flag absent ('{Truncate(current.Text, 100)}').";
    }

    private static List<string> MissingExecutableWarnings(IReadOnlyList<RegistrySnapshotEntry> entries)
    {
        var warnings = new List<string>();
        if (!entries.Any(entry => IsExecutable(entry.ValueName, ShippingFileName)))
        {
            warnings.Add(
                $"{ShippingFileName} was not located, so only the launcher was configured. " +
                "The game process may not receive the compatibility flag until the game binary is found.");
        }

        if (!entries.Any(entry => IsExecutable(entry.ValueName, LauncherFileName)))
        {
            warnings.Add($"{LauncherFileName} was not located; only the game binary was configured.");
        }

        return warnings;
    }

    private static bool IsExecutable(string path, string fileName) =>
        string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    private async Task<IReadOnlyList<string>> ResolveTargetsAsync(CancellationToken cancellationToken)
    {
        ValorantInstallation installation = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);

        var targets = new List<string>(capacity: 2);
        AddTarget(targets, installation.ExecutablePath);
        AddTarget(targets, installation.ShippingExecutablePath);
        return targets;
    }

    private static void AddTarget(List<string> targets, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalized = path.Trim();
        if (targets.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        targets.Add(normalized);
    }

    private readonly record struct CurrentLayer(
        bool Exists,
        bool IsString,
        string? Text,
        bool HasFlag,
        RegistryValueKind Kind);
}
