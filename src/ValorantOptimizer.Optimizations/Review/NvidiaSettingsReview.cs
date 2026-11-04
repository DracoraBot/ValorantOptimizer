using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Read-only review of the NVIDIA display driver and the control-panel settings around it.
/// The toolkit intentionally does not automate NVIDIA Control Panel, so the card reports what
/// Windows records about the driver and explains why the rest is a manual decision.
/// </summary>
public sealed class NvidiaSettingsReview : IOptimization
{
    public const string OptimizationId = "nvidia-settings";

    /// <summary>Display adapters class GUID used by the Windows driver store.</summary>
    public const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public const string DriverDescriptionValueName = "DriverDesc";
    public const string ProviderNameValueName = "ProviderName";
    public const string DriverVersionValueName = "DriverVersion";

    private const int MaximumAdapterEvidenceEntries = 4;

    private const string BlockedReason =
        "G-SYNC, power management mode, texture filtering and low latency mode are driver- and " +
        "preference-dependent. This toolkit intentionally does not automate NVIDIA Control Panel: " +
        "the right combination depends on the monitor, GPU, thermals and how you play. Review those " +
        "settings in NVIDIA Control Panel yourself.";

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = OptimizationId,
        Name = "NVIDIA driver settings (review)",
        Category = OptimizationCategory.Gpu,
        Risk = RiskLevel.Experimental,
        Summary = "Review NVIDIA adapter and driver details plus the control-panel settings that affect VALORANT; the toolkit never automates NVIDIA Control Panel.",
        WhatItChanges =
            @"Nothing. This read-only card reads DriverDesc, ProviderName and DriverVersion from " +
            @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318} " +
            "subkeys and reports what Windows records. No driver setting, NVIDIA profile or registry value is written.",
        Why =
            "NVIDIA's control panel exposes G-SYNC, power management mode, texture filtering quality, " +
            "low latency mode and other options that can change frame pacing or latency on some systems. " +
            "The useful combination depends on the display, GPU model, cooling and personal preference, " +
            "and NVIDIA does not provide a supported automation interface for this toolkit. The card " +
            "therefore surfaces the detected driver and leaves the decisions manual; no frame-rate " +
            "change is promised.",
        Compatibility =
            "NVIDIA display adapters that expose the standard display-driver class entries. Laptop, " +
            "hybrid-GPU and OEM driver stacks may store different or additional values, and the " +
            "control-panel options available depend on the driver version and monitor.",
        StateWhenApplied = "Read-only review",
        Restart = RestartRequirement.None,
        RequiresElevation = false,
        ManagedScopes = [],
        Notes =
        [
            "Read-only: apply and restore are refused by design, and the card is disabled in the UI.",
            "G-SYNC and refresh-rate choices depend on the monitor, cable and display connection.",
            "Power management mode and texture filtering are preferences; verify them in NVIDIA Control Panel.",
            "Driver version strings are informational only and do not imply any recommended build.",
        ],
    };

    public Task<OptimizationAssessment> AssessAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        NvidiaSnapshot snapshot = ReadSnapshot(context.Registry);

        if (snapshot.Error)
        {
            var unavailable = OptimizationAssessment.Review(
                BlockedReason,
                "NVIDIA settings review is unavailable") with
            {
                Explanation =
                    "The display-driver class registry could not be read, so the card does not guess whether " +
                    "an NVIDIA adapter is present. Nothing is changed by this card.",
                Evidence =
                [
                    new AssessmentEvidence("NVIDIA GPU", "Unavailable"),
                    new AssessmentEvidence("Display driver entries", "Unavailable"),
                ],
            };
            return Task.FromResult(unavailable);
        }

        if (snapshot.Adapters.Count == 0)
        {
            OptimizationAssessment unsupported = OptimizationAssessment.Unsupported(
                "No NVIDIA display adapter was detected in the Windows display-driver class registry. " +
                "This review card only applies to NVIDIA GPUs and always stays read-only.",
                "No NVIDIA display adapter detected") with
            {
                Evidence =
                [
                    new AssessmentEvidence("NVIDIA GPU", "Not detected"),
                    new AssessmentEvidence(
                        "Display driver entries",
                        $"{snapshot.SubKeyCount} display-driver entr{(snapshot.SubKeyCount == 1 ? "y" : "ies")} inspected"),
                ],
            };
            return Task.FromResult(unsupported);
        }

        var evidence = new List<AssessmentEvidence>();
        foreach ((NvidiaAdapter adapter, int index) in snapshot.Adapters
                     .Take(MaximumAdapterEvidenceEntries)
                     .Select((adapter, index) => (adapter, index + 1)))
        {
            evidence.Add(new AssessmentEvidence(
                $"NVIDIA GPU {index}",
                $"{adapter.Description} (provider: {adapter.Provider ?? "unknown"}, driver: {adapter.DriverVersion ?? "unknown"})"));
        }

        evidence.Add(new AssessmentEvidence(
            "Display driver entries",
            $"{snapshot.SubKeyCount} inspected; {snapshot.Adapters.Count} NVIDIA adapter{(snapshot.Adapters.Count == 1 ? string.Empty : "s")} matched"));

        string firstDescription = snapshot.Adapters[0].Description;
        string headline = snapshot.Adapters.Count == 1
            ? $"NVIDIA driver detected: {firstDescription}"
            : $"NVIDIA drivers detected: {snapshot.Adapters.Count} adapters";

        OptimizationAssessment assessment = OptimizationAssessment.Review(BlockedReason, headline) with
        {
            Explanation =
                "Windows records the NVIDIA adapter and driver above. G-SYNC, power management mode, " +
                "texture filtering and low latency mode are controlled by NVIDIA Control Panel and are " +
                "driver- and preference-dependent, so this read-only card does not change them. Open " +
                "NVIDIA Control Panel to review them for your display and GPU.",
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

        NvidiaSnapshot snapshot = ReadSnapshot(context.Registry);
        string adapters = snapshot.Adapters.Count == 0
            ? "none"
            : string.Join(
                " | ",
                snapshot.Adapters.Select(adapter =>
                    $"{adapter.Description} (provider={adapter.Provider ?? "unknown"}, driver={adapter.DriverVersion ?? "unknown"})"));

        ChangeSet changeSet = ReviewOnlySupport.CreateChangeSet(
            Descriptor.Id,
            ("nvidiaDetected", snapshot.Adapters.Count > 0 ? "true" : "false"),
            ("adapterCount", ReviewOnlySupport.Invariant(snapshot.Adapters.Count)),
            ("displayDriverEntries", ReviewOnlySupport.Invariant(snapshot.SubKeyCount)),
            ("adapters", adapters));

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

    private static NvidiaSnapshot ReadSnapshot(IRegistryStore registry)
    {
        IReadOnlyList<string>? subKeys = ReviewOnlySupport.TryGetSubKeyNames(
            registry,
            RegistryHive.LocalMachine,
            DisplayClassKey);

        if (subKeys is null)
        {
            return new NvidiaSnapshot([], 0, Error: true);
        }

        var adapters = new List<NvidiaAdapter>();
        foreach (string subKey in subKeys)
        {
            string key = DisplayClassKey + "\\" + subKey;
            string? description = ReviewOnlySupport.TryReadText(
                registry,
                RegistryHive.LocalMachine,
                key,
                DriverDescriptionValueName);
            string? provider = ReviewOnlySupport.TryReadText(
                registry,
                RegistryHive.LocalMachine,
                key,
                ProviderNameValueName);
            string? driverVersion = ReviewOnlySupport.TryReadText(
                registry,
                RegistryHive.LocalMachine,
                key,
                DriverVersionValueName);

            if (!ContainsNvidia(description) && !ContainsNvidia(provider))
            {
                continue;
            }

            adapters.Add(new NvidiaAdapter(
                string.IsNullOrWhiteSpace(description) ? "NVIDIA display adapter" : description,
                provider,
                driverVersion));
        }

        return new NvidiaSnapshot(adapters, subKeys.Count, Error: false);
    }

    private static bool ContainsNvidia(string? text) =>
        text?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true;

    private sealed record NvidiaAdapter(string Description, string? Provider, string? DriverVersion);

    private sealed record NvidiaSnapshot(
        IReadOnlyList<NvidiaAdapter> Adapters,
        int SubKeyCount,
        bool Error);
}
