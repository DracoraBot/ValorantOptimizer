using System.Globalization;
using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Diagnostics;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Sets the Windows per-application graphics preference for the VALORANT executables to
/// "High performance" (GpuPreference=2). The module only writes per-executable values under
/// <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c>; it never changes global Windows
/// graphics settings, driver settings or policies.
/// </summary>
public sealed class ValorantGpuPreferenceOptimization : IOptimization
{
    internal const string UserGpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string LauncherFileName = "VALORANT.exe";
    private const string ShippingFileName = "VALORANT-Win64-Shipping.exe";

    private readonly IValorantLocator _locator;

    public ValorantGpuPreferenceOptimization(IValorantLocator locator)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = "valorant-gpu-preference",
        Name = "VALORANT high-performance GPU preference",
        Category = OptimizationCategory.Valorant,
        Risk = RiskLevel.Safe,
        Summary = "Ask Windows to run VALORANT on the high-performance GPU.",
        WhatItChanges =
            @"Writes per-executable values under HKCU\Software\Microsoft\DirectX\UserGpuPreferences " +
            "with GpuPreference=2 (High performance) for VALORANT.exe and, when found, " +
            "VALORANT-Win64-Shipping.exe. Each executable's other stored pairs are preserved. " +
            "No global Windows graphics setting or GPU driver setting is modified.",
        Why =
            "On laptops and multi-GPU desktops Windows can choose the integrated GPU for an app. " +
            "Marking the game executables as high performance asks Windows to pick the discrete GPU, " +
            "which can help frame consistency on some systems. The result depends on the GPU " +
            "configuration, driver and whether the vendor control panel overrides this preference.",
        Compatibility =
            "Windows 10/11 with a VALORANT installation the locator can find. Meaningful mainly on " +
            "systems with more than one GPU; NVIDIA/AMD control panels may override or duplicate this preference.",
        StateWhenApplied = "High performance",
        Restart = RestartRequirement.None,
        RequiresElevation = false,
        ManagedScopes = [new RegistryScope(RegistryHive.CurrentUser, UserGpuPreferencesKey)],
        Notes =
        [
            "This is a per-executable preference only; it does not change Windows' global graphics settings.",
            "Both VALORANT.exe (launcher) and VALORANT-Win64-Shipping.exe (game binary) are targeted when located.",
            "The preference is read when an executable starts, so restart the launcher/game; no reboot is required.",
            "GPU vendor control panels or Windows Settings > Display > Graphics may override or duplicate this entry.",
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
                    "No VALORANT executable was located, so no per-game graphics preference can be set. " +
                    "Install VALORANT (or re-run the scan) and try again.",
            };
        }

        var evidence = new List<AssessmentEvidence>();
        bool allApplied = true;
        foreach (string target in targets)
        {
            CurrentPreference current = ReadPreference(context.Registry, target);
            allApplied &= current.IsHighPerformance;
            evidence.Add(new AssessmentEvidence(Path.GetFileName(target), DescribePreference(current)));
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
                "Not found; only the launcher will be configured, so the game process may not pick up the preference."));
        }

        string explanation = allApplied
            ? "Windows is asked to use the high-performance GPU for the configured VALORANT executables. " +
              "GPU vendor control panels may override this preference."
            : "Applying writes a Windows per-app preference only; it does not change global graphics settings. " +
              "It takes effect the next time each configured executable starts.";

        if (!launcherFound)
        {
            explanation += " The launcher (VALORANT.exe) was not located; only the game binary will be configured.";
        }

        if (!shippingFound)
        {
            explanation += " The game binary (VALORANT-Win64-Shipping.exe) was not located; only the launcher " +
                           "will be configured, so the game process may not use this preference.";
        }

        return new OptimizationAssessment
        {
            State = allApplied ? OptimizationState.Applied : OptimizationState.NotApplied,
            Headline = allApplied
                ? "High-performance GPU preference is set for VALORANT"
                : "Windows graphics preference is not set for VALORANT",
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
                "No VALORANT executable was located, so there is no graphics-preference state to capture.");
        }

        List<RegistrySnapshotEntry> entries = targets
            .Select(target => RegistrySnapshot.Capture(
                context.Registry,
                RegistryHive.CurrentUser,
                UserGpuPreferencesKey,
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
                "No captured VALORANT graphics-preference targets were supplied, so nothing was changed."));
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

            CurrentPreference current = ReadPreference(context.Registry, entry.ValueName);
            string existing = current.IsString ? current.Text ?? string.Empty : string.Empty;
            string merged = UserGpuPreferencesData.Merge(existing);

            if (current.IsString && string.Equals(current.Text, merged, StringComparison.Ordinal))
            {
                changes.Add($"{entry.ValueName} already had GpuPreference=2.");
                continue;
            }

            context.Registry.WriteValue(
                RegistryHive.CurrentUser,
                UserGpuPreferencesKey,
                entry.ValueName,
                RegistryValueKind.String,
                merged);
            changes.Add($"Set {entry.ValueName} to '{merged}'.");
        }

        return Task.FromResult(new OperationOutcome
        {
            Success = true,
            Message =
                $"Set the Windows high-performance graphics preference for {original.Entries.Count} " +
                "VALORANT executable(s). It takes effect the next time each executable starts; " +
                "no reboot is required.",
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
                "The backup does not contain any VALORANT graphics-preference entries to restore."));
        }

        if (original.Entries.Any(entry => !IsManagedEntry(entry)))
        {
            return Task.FromResult(OperationOutcome.Failed(
                "The backup contains registry entries outside this optimization's scope, " +
                "so it was not restored."));
        }

        RegistrySnapshot.RestoreAll(context.Registry, original.Entries);

        if (!ChangeSetInspector.Matches(context.Registry, original, out List<string> mismatches))
        {
            var failed = OperationOutcome.Failed(
                "The original VALORANT graphics-preference values could not be restored exactly.",
                mismatches.ToArray());
            return Task.FromResult(failed);
        }

        return Task.FromResult(new OperationOutcome
        {
            Success = true,
            Verified = true,
            Message =
                $"Restored {original.Entries.Count} VALORANT graphics-preference entr" +
                $"{(original.Entries.Count == 1 ? "y" : "ies")} to the exact state captured before the change.",
        });
    }

    private static bool IsManagedEntry(RegistrySnapshotEntry entry) =>
        entry.Hive == RegistryHive.CurrentUser &&
        string.Equals(entry.SubKey, UserGpuPreferencesKey, StringComparison.OrdinalIgnoreCase);

    private static CurrentPreference ReadPreference(IRegistryStore registry, string executablePath)
    {
        RegistryValue? value = registry.ReadValue(RegistryHive.CurrentUser, UserGpuPreferencesKey, executablePath);
        if (value is null)
        {
            return default;
        }

        bool isString = value.Kind is RegistryValueKind.String or RegistryValueKind.ExpandString;
        string? text = isString ? Convert.ToString(value.Data, CultureInfo.InvariantCulture) : null;
        return new CurrentPreference(
            Exists: true,
            IsString: isString,
            Text: text,
            Preference: isString ? UserGpuPreferencesData.TryGetPreference(text) : null);
    }

    private static string DescribePreference(CurrentPreference current)
    {
        if (!current.Exists)
        {
            return "Not configured (Windows chooses the GPU).";
        }

        if (!current.IsString)
        {
            return "Stored with an unexpected registry type; applying replaces it with GpuPreference=2.";
        }

        if (current.Preference is null)
        {
            return string.IsNullOrWhiteSpace(current.Text)
                ? "Value exists but is empty."
                : $"No GpuPreference pair in stored data '{Truncate(current.Text, 80)}'.";
        }

        return current.Preference.Value switch
        {
            0 => "GpuPreference=0 (Let Windows decide).",
            1 => "GpuPreference=1 (Power saving).",
            UserGpuPreferencesData.HighPerformance => "GpuPreference=2 (High performance).",
            _ => $"GpuPreference={current.Preference.Value} (unrecognized value).",
        };
    }

    private static List<string> MissingExecutableWarnings(IReadOnlyList<RegistrySnapshotEntry> entries)
    {
        var warnings = new List<string>();
        if (!entries.Any(entry => IsExecutable(entry.ValueName, ShippingFileName)))
        {
            warnings.Add(
                $"{ShippingFileName} was not located, so only the launcher was configured. " +
                "The game process may not use this preference until the game binary is found.");
        }

        if (!entries.Any(entry => IsExecutable(entry.ValueName, LauncherFileName)))
        {
            warnings.Add($"{LauncherFileName} was not located; only the game binary was configured.");
        }

        return warnings;
    }

    private static bool IsExecutable(string path, string fileName) =>
        string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

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

        // The locator contract returns a full path; trim only so tests/fakes see the exact value.
        string normalized = path.Trim();
        if (targets.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        targets.Add(normalized);
    }

    private readonly record struct CurrentPreference(
        bool Exists,
        bool IsString,
        string? Text,
        int? Preference)
    {
        public bool IsHighPerformance =>
            IsString && Preference == UserGpuPreferencesData.HighPerformance;
    }
}
