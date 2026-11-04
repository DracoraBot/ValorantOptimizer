using System.Globalization;
using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Sets Delivery Optimization to HTTP-only downloads so Windows Update, Microsoft Store and other
/// Delivery Optimization clients do not use peer-to-peer or LAN sharing. A Group Policy override is
/// detected and never modified.
/// </summary>
public sealed class DeliveryOptimizationPeerOptimization : IOptimization
{
    public const string OptimizationId = "delivery-optimization-peer";
    public const string ConfigSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config";
    public const string ValueName = "DODownloadMode";
    public const string PolicySubKey = @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";
    public const string PolicyDownloadModeValueName = "DODownloadMode";
    public const string PolicyLegacyDownloadModeValueName = "DownloadMode";
    public const string ServiceSubKey = @"SYSTEM\CurrentControlSet\Services\DoSvc";

    private const int HttpOnlyMode = 0;

    private static readonly RegistryTarget ConfigTarget =
        new(RegistryHive.LocalMachine, ConfigSubKey, ValueName);

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = OptimizationId,
        Name = "Disable Delivery Optimization peer downloads",
        Category = OptimizationCategory.Windows,
        Risk = RiskLevel.Advanced,
        Summary = "Restricts Delivery Optimization to HTTP-only downloads so Windows Update and Store content is not fetched from other PCs on your network or the internet.",
        WhatItChanges = @"Writes the DWORD HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config\DODownloadMode = 0 (HTTP only, no peering). Undo restores the previous value, or deletes it when it did not exist. The Group Policy key is only read for the managed-by-policy check and is never written.",
        Why = "By default Delivery Optimization can source update and Store content from other PCs on the LAN or the internet, which uses local upload bandwidth and disk activity. HTTP-only mode removes that peer path so downloads come from Microsoft's servers. The setting affects update/content delivery traffic, not VALORANT gameplay traffic, and it does not promise higher FPS or lower in-game latency.",
        Compatibility = "Windows 10 and Windows 11 editions that include Delivery Optimization; requires administrator rights. A Group Policy under HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization overrides the local value, in which case the module reports Review required. SKUs without Delivery Optimization report Unsupported.",
        StateWhenApplied = "Peer downloads off",
        Restart = RestartRequirement.None,
        RequiresElevation = true,
        ManagedScopes = [new RegistryScope(RegistryHive.LocalMachine, ConfigSubKey)],
        Notes =
        [
            "This affects Windows Update, Microsoft Store and other Delivery Optimization downloads - not VALORANT network traffic or gameplay.",
            "HTTP-only downloads use a single Microsoft source; on some connections this can be slower than peer-assisted delivery, but it stops other PCs from pulling content from yours (and vice versa).",
            "Safe and reversible with no reboot. New downloads use the new mode immediately; downloads already in flight may finish under the previous mode.",
            "If DODownloadMode or DownloadMode is set by policy, the policy wins and the toolkit leaves it untouched.",
        ],
    };

    public Task<OptimizationAssessment> AssessAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        RegistryValue? config = context.Registry.ReadValue(
            RegistryHive.LocalMachine,
            ConfigSubKey,
            ValueName);

        var evidence = new List<AssessmentEvidence>
        {
            new(ValueName, DescribeModeValue(config)),
        };

        (string? overrideReason, List<AssessmentEvidence> policyEvidence) = InspectPolicy(context.Registry);
        evidence.AddRange(policyEvidence);

        if (overrideReason is not null)
        {
            return Task.FromResult(OptimizationAssessment.Review(overrideReason, "Managed by policy") with
            {
                Evidence = evidence,
            });
        }

        bool configKeyExists = context.Registry.KeyExists(RegistryHive.LocalMachine, ConfigSubKey);
        bool serviceKeyExists = context.Registry.KeyExists(RegistryHive.LocalMachine, ServiceSubKey);
        if (!configKeyExists && !serviceKeyExists)
        {
            return Task.FromResult(OptimizationAssessment.Unsupported(
                "Delivery Optimization is not present on this Windows edition: neither the Config key nor the DoSvc service key exists, so there is nothing to configure.",
                "Delivery Optimization not available") with
            {
                Evidence = evidence,
            });
        }

        OptimizationAssessment assessment;
        if (config is null)
        {
            assessment = OptimizationAssessment.Available(
                "Peer downloads enabled (Windows default)",
                "DODownloadMode is not set, so Windows uses its default Delivery Optimization mode, which can include peer-to-peer delivery.");
        }
        else if (config.Kind != RegistryValueKind.DWord)
        {
            assessment = OptimizationAssessment.Available(
                "Peer downloads enabled (unexpected format)",
                $"DODownloadMode is stored as {config.Kind} instead of DWORD, so the recommended HTTP-only DWORD 0 is not in effect. Applying writes DWORD 0 and Undo restores the original kind and data.");
        }
        else
        {
            int mode = Convert.ToInt32(config.Data, CultureInfo.InvariantCulture);
            assessment = mode == HttpOnlyMode
                ? OptimizationAssessment.Applied(
                    "Peer downloads disabled",
                    "DODownloadMode is 0: Delivery Optimization uses HTTP servers only, with no peer-to-peer or LAN sharing.")
                : OptimizationAssessment.Available(
                    $"Peer downloads enabled (mode {mode})",
                    $"DODownloadMode is {mode} ({DescribeMode(mode)}), so Delivery Optimization can use peer downloads. Applying sets it to 0, HTTP only.");
        }

        return Task.FromResult(assessment with { Evidence = evidence });
    }

    public Task<ChangeSet> CaptureAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var original = new ChangeSet
        {
            OptimizationId = Descriptor.Id,

            // Only the local Config value is captured; policy values are never ours to restore.
            Entries = RegistrySnapshot.CaptureMany(context.Registry, ConfigTarget),
        };

        return Task.FromResult(original);
    }

    public Task<OperationOutcome> ApplyAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(original);
        cancellationToken.ThrowIfCancellationRequested();

        // Re-check at apply time: a policy that appeared after capture must not be overridden locally.
        (string? overrideReason, _) = InspectPolicy(context.Registry);
        if (overrideReason is not null)
        {
            return Task.FromResult(OperationOutcome.Failed(overrideReason));
        }

        bool configKeyExists = context.Registry.KeyExists(RegistryHive.LocalMachine, ConfigSubKey);
        bool serviceKeyExists = context.Registry.KeyExists(RegistryHive.LocalMachine, ServiceSubKey);
        if (!configKeyExists && !serviceKeyExists)
        {
            return Task.FromResult(OperationOutcome.Failed(
                "Delivery Optimization is not present on this Windows edition, so there is nothing to configure."));
        }

        context.Registry.WriteValue(
            RegistryHive.LocalMachine,
            ConfigSubKey,
            ValueName,
            RegistryValueKind.DWord,
            HttpOnlyMode);

        return Task.FromResult(OperationOutcome.Ok(
            "Disabled Delivery Optimization peer downloads (HTTP only).") with
        {
            Changes =
            [
                $@"{RegistryHive.LocalMachine}\{ConfigSubKey}\{ValueName} = {HttpOnlyMode} (REG_DWORD, HTTP only)",
            ],
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

        // Restore the local Config value only; policy values were never captured or modified.
        RegistrySnapshot.RestoreAll(context.Registry, original.Entries);

        return Task.FromResult(OperationOutcome.Ok(
            "Restored the original Delivery Optimization download mode.") with
        {
            Changes = original.Entries.Select(entry => entry.ToString()).ToList(),
        });
    }

    private static (string? OverrideReason, List<AssessmentEvidence> Evidence) InspectPolicy(IRegistryStore registry)
    {
        var evidence = new List<AssessmentEvidence>();
        string? overrideReason = null;

        (string ValueName, RegistryValue? Value)[] policyValues =
        [
            (PolicyDownloadModeValueName, registry.ReadValue(RegistryHive.LocalMachine, PolicySubKey, PolicyDownloadModeValueName)),
            (PolicyLegacyDownloadModeValueName, registry.ReadValue(RegistryHive.LocalMachine, PolicySubKey, PolicyLegacyDownloadModeValueName)),
        ];

        foreach ((string valueName, RegistryValue? value) in policyValues)
        {
            if (value is null)
            {
                continue;
            }

            evidence.Add(new AssessmentEvidence($"Policy {valueName}", DescribeModeValue(value)));

            if (value.Kind != RegistryValueKind.DWord)
            {
                overrideReason ??=
                    $"Delivery Optimization is managed by policy: '{valueName}' under HKLM\\{PolicySubKey} is stored as {value.Kind} instead of DWORD, so the effective download mode cannot be determined. The local setting is not changed.";
                continue;
            }

            int policyMode = Convert.ToInt32(value.Data, CultureInfo.InvariantCulture);
            if (policyMode != HttpOnlyMode)
            {
                overrideReason ??=
                    $"Delivery Optimization is managed by policy: '{valueName}' = {policyMode} under HKLM\\{PolicySubKey}. The local DODownloadMode value is ignored while this policy is set, so peer downloads can only be managed by changing the policy. The policy was not modified.";
            }
        }

        return (overrideReason, evidence);
    }

    private static string DescribeModeValue(RegistryValue? value)
    {
        if (value is null)
        {
            return "Not set (Windows default: peer downloads allowed)";
        }

        if (value.Kind != RegistryValueKind.DWord)
        {
            return $"{RegistryValueCodec.Serialize(value.Data, value.Kind)} ({value.Kind})";
        }

        int mode = Convert.ToInt32(value.Data, CultureInfo.InvariantCulture);
        return $"{mode} ({DescribeMode(mode)})";
    }

    private static string DescribeMode(int mode) => mode switch
    {
        HttpOnlyMode => "HTTP only, no peering",
        1 => "LAN peers",
        2 => "group peers",
        3 => "internet peers",
        _ => "custom mode",
    };
}
