using System.Globalization;
using System.Security;
using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Stops Windows from turning off physical Ethernet/Wi-Fi adapters to save power by setting
/// <c>PnPCapabilities</c> (DWORD 24) on each adapter instance under the network class key.
/// Virtual, VPN, WAN Miniport, Hyper-V, TAP and loopback adapters are deliberately skipped.
/// </summary>
public sealed class NetworkAdapterPowerOptimization : IOptimization
{
    internal const string AdapterClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    internal const string DriverDescValueName = "DriverDesc";
    internal const string PnpCapabilitiesValueName = "PnPCapabilities";
    internal const string CharacteristicsValueName = "Characteristics";
    internal const string IfTypeValueName = "*IfType";

    /// <summary>0x18: do not allow Windows to turn the device off to save power.</summary>
    internal const uint RecommendedPnpCapabilities = 24;

    private const uint NcfVirtual = 0x1;
    private const uint NcfSoftwareEnumerated = 0x2;
    private const uint NcfPhysical = 0x4;
    private const uint IfTypeEthernet = 6;
    private const uint IfTypeIeee80211 = 71;

    private static readonly string[] VirtualAdapterMarkers =
    [
        "wan miniport",
        "miniport",
        "hyper-v",
        "hyper v",
        "virtual",
        "vmware",
        "virtualbox",
        "loopback",
        "tap-windows",
        "tap adapter",
        "vpn",
        "tunnel",
        "wireguard",
        "wintun",
        "npcap",
        "pseudo",
        "kernel debug",
        "wi-fi direct",
        "wifi direct",
        "bluetooth",
        "6to4",
        "isatap",
        "teredo",
        "pptp",
        "l2tp",
        "sstp",
        "ikev2",
        "ras async",
        "wireless wan",
        "multiplexor",
    ];

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = "network-adapter-power",
        Name = "Keep network adapters powered on",
        Category = OptimizationCategory.Network,
        Risk = RiskLevel.Advanced,
        Summary = "Stop Windows from turning off physical Ethernet and Wi-Fi adapters to save power.",
        WhatItChanges =
            @"Sets PnPCapabilities (DWORD 24 / 0x18) on each physical Ethernet or Wi-Fi adapter " +
            @"under HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}. " +
            "This disables Windows' \"Allow the computer to turn off this device to save power\" option " +
            "for those adapters. Virtual, VPN, WAN Miniport, Hyper-V, TAP, loopback and Bluetooth adapters " +
            "are skipped, and no driver, protocol or virtual adapter setting is changed.",
        Why =
            "Windows can power down a network adapter when it is idle. A power-saving adapter may take " +
            "longer to respond when traffic arrives. Keeping physical adapters powered can reduce connection " +
            "stalls on some systems, but it increases idle power use and is hardware/driver dependent. No " +
            "latency or frame-rate improvement is promised.",
        Compatibility =
            "Windows 10/11 with at least one physical Ethernet or Wi-Fi adapter recognized by Windows. " +
            "Virtual/VPN adapters are intentionally skipped. Some drivers may require the adapter to be " +
            "disabled and re-enabled, or a reboot, before the change takes effect.",
        StateWhenApplied = "Windows cannot power off adapters",
        Restart = RestartRequirement.None,
        RequiresElevation = true,
        ManagedScopes = [new RegistryScope(RegistryHive.LocalMachine, AdapterClassKey)],
        Notes =
        [
            "Only physical Ethernet/Wi-Fi adapters are targeted; virtual, VPN, WAN Miniport, Hyper-V, TAP and loopback adapters are skipped.",
            "Writes PnPCapabilities = 24 (0x18) per adapter, disabling Windows' device power-off option.",
            "Restore returns each adapter's exact original value, kind and existence; values that did not exist are deleted.",
            "May increase idle power use; some drivers may only apply the change after the adapter is re-enabled or the PC restarts.",
            "No latency or FPS improvement is guaranteed.",
        ],
    };

    public Task<OptimizationAssessment> AssessAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<AdapterTarget> adapters = FindTargetAdapters(context.Registry);
        if (adapters.Count == 0)
        {
            return Task.FromResult(OptimizationAssessment.Unsupported(
                "No physical Ethernet or Wi-Fi network adapters were found. Virtual, VPN, WAN Miniport, " +
                "Hyper-V, TAP and loopback adapters are skipped, so there is nothing safe to configure.",
                "No physical network adapters found"));
        }

        bool allApplied = true;
        var evidence = new List<AssessmentEvidence>(adapters.Count);
        foreach (AdapterTarget adapter in adapters)
        {
            AdapterCapabilities current = ReadCapabilities(context.Registry, adapter.SubKey);
            allApplied &= current.IsRecommended;
            evidence.Add(new AssessmentEvidence(adapter.DriverDesc, DescribeCapabilities(current)));
        }

        return Task.FromResult(new OptimizationAssessment
        {
            State = allApplied ? OptimizationState.Applied : OptimizationState.NotApplied,
            Headline = allApplied
                ? "Windows cannot power off the targeted network adapters"
                : "Windows may power off some network adapters",
            Explanation =
                "Applying sets PnPCapabilities = 24 (0x18) on physical Ethernet/Wi-Fi adapters under the " +
                "network class key, disabling Windows' \"Allow the computer to turn off this device to save " +
                "power\" option for them. This changes power behavior system-wide, may increase idle power " +
                "use, and is hardware/driver dependent. No reboot is required, though some drivers may only " +
                "notice the change after the adapter is re-enabled. No latency or FPS gain is promised.",
            Evidence = evidence,
        });
    }

    public Task<ChangeSet> CaptureAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<AdapterTarget> adapters = FindTargetAdapters(context.Registry);
        if (adapters.Count == 0)
        {
            throw new InvalidOperationException(
                "No physical Ethernet or Wi-Fi network adapters were found, so there is no adapter power " +
                "state to capture.");
        }

        List<RegistrySnapshotEntry> entries = adapters
            .Select(adapter => RegistrySnapshot.Capture(
                context.Registry,
                RegistryHive.LocalMachine,
                adapter.SubKey,
                PnpCapabilitiesValueName))
            .ToList();

        return Task.FromResult(new ChangeSet
        {
            OptimizationId = Descriptor.Id,
            Entries = entries,
        });
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
                "No captured network adapter values were supplied, so nothing was changed."));
        }

        if (original.Entries.Any(entry => !IsManagedEntry(entry)))
        {
            return Task.FromResult(OperationOutcome.Failed(
                "The captured state contains entries outside this optimization's registry scope, " +
                "so nothing was changed."));
        }

        var changes = new List<string>(original.Entries.Count);
        foreach (RegistrySnapshotEntry entry in original.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AdapterCapabilities current = ReadCapabilities(context.Registry, entry.SubKey);
            if (current.IsRecommended)
            {
                changes.Add($"{entry.SubKey}: PnPCapabilities already 24 (0x18).");
                continue;
            }

            context.Registry.WriteValue(
                RegistryHive.LocalMachine,
                entry.SubKey,
                PnpCapabilitiesValueName,
                RegistryValueKind.DWord,
                RegistryValueCodec.ToRegistryDword(RecommendedPnpCapabilities));
            changes.Add($"{entry.SubKey}: PnPCapabilities set to 24 (0x18).");
        }

        return Task.FromResult(new OperationOutcome
        {
            Success = true,
            Message =
                $"Disabled Windows device power-off for {original.Entries.Count} network adapter(s). " +
                "This may increase idle power use. Some drivers apply the change only after the adapter " +
                "is disabled and re-enabled, or after a reboot; no FPS gain is promised.",
            Changes = changes,
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
                "The backup does not contain any network adapter values to restore."));
        }

        if (original.Entries.Any(entry => !IsManagedEntry(entry)))
        {
            return Task.FromResult(OperationOutcome.Failed(
                "The backup contains registry entries outside this optimization's scope, so it was not restored."));
        }

        RegistrySnapshot.RestoreAll(context.Registry, original.Entries);

        if (!ChangeSetInspector.Matches(context.Registry, original, out List<string> mismatches))
        {
            return Task.FromResult(OperationOutcome.Failed(
                "The original network adapter power values could not be restored exactly.",
                mismatches.ToArray()));
        }

        return Task.FromResult(new OperationOutcome
        {
            Success = true,
            Verified = true,
            Message =
                $"Restored {original.Entries.Count} network adapter power entr" +
                $"{(original.Entries.Count == 1 ? "y" : "ies")} to the exact state captured before the change.",
        });
    }

    private static IReadOnlyList<AdapterTarget> FindTargetAdapters(IRegistryStore registry)
    {
        IReadOnlyList<string> instanceNames;
        try
        {
            instanceNames = registry.GetSubKeyNames(RegistryHive.LocalMachine, AdapterClassKey);
        }
        catch (Exception ex) when (IsExpectedRegistryFailure(ex))
        {
            return [];
        }

        var targets = new List<AdapterTarget>();
        foreach (string instanceName in instanceNames)
        {
            string subKey = AdapterClassKey + "\\" + instanceName;
            try
            {
                string? driverDesc = ReadString(registry, subKey, DriverDescValueName);
                if (string.IsNullOrWhiteSpace(driverDesc) || !IsPhysicalAdapter(registry, subKey, driverDesc))
                {
                    continue;
                }

                targets.Add(new AdapterTarget(subKey, instanceName, driverDesc.Trim()));
            }
            catch (Exception ex) when (IsExpectedRegistryFailure(ex))
            {
                // Some adapter instances have restricted ACLs even for read access. Skip the
                // instance rather than failing the whole assessment; the user still sees the
                // remaining adapters and the toggle stays safely unavailable/available as a whole.
            }
        }

        return targets;
    }

    private static bool IsExpectedRegistryFailure(Exception exception) =>
        exception is SecurityException
            or UnauthorizedAccessException
            or IOException;

    private static bool IsPhysicalAdapter(IRegistryStore registry, string subKey, string driverDesc)
    {
        foreach (string marker in VirtualAdapterMarkers)
        {
            if (driverDesc.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        uint? characteristics = ReadNumeric(registry, subKey, CharacteristicsValueName);
        if (characteristics is uint flags)
        {
            bool virtualOrSoftware = (flags & (NcfVirtual | NcfSoftwareEnumerated)) != 0;
            bool physical = (flags & NcfPhysical) != 0;
            if (virtualOrSoftware && !physical)
            {
                return false;
            }
        }

        // *IfType is an NDIS interface type: 6 = Ethernet, 71 = IEEE 802.11 (Wi-Fi).
        // Only reject adapters whose recorded type is a different (virtual/tunnel/loopback) kind;
        // a missing value is not treated as proof either way.
        uint? ifType = ReadNumeric(registry, subKey, IfTypeValueName);
        if (ifType is uint type && type is not IfTypeEthernet and not IfTypeIeee80211)
        {
            return false;
        }

        return true;
    }

    private static string? ReadString(IRegistryStore registry, string subKey, string valueName)
    {
        RegistryValue? value = registry.ReadValue(RegistryHive.LocalMachine, subKey, valueName);
        if (value is null || value.Kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
        {
            return null;
        }

        return Convert.ToString(value.Data, CultureInfo.InvariantCulture);
    }

    private static uint? ReadNumeric(IRegistryStore registry, string subKey, string valueName)
    {
        RegistryValue? value = registry.ReadValue(RegistryHive.LocalMachine, subKey, valueName);
        if (value is null || value.Kind is not (RegistryValueKind.DWord or RegistryValueKind.QWord))
        {
            return null;
        }

        return AsUInt32(value.Data);
    }

    private static uint? AsUInt32(object? data) => data switch
    {
        int signed => unchecked((uint)signed),
        uint unsigned => unsigned,
        short signedShort => unchecked((uint)signedShort),
        ushort unsignedShort => unsignedShort,
        long signedLong => unchecked((uint)signedLong),
        ulong unsignedLong => unchecked((uint)unsignedLong),
        _ => null,
    };

    private static AdapterCapabilities ReadCapabilities(IRegistryStore registry, string subKey)
    {
        RegistryValue? value = registry.ReadValue(RegistryHive.LocalMachine, subKey, PnpCapabilitiesValueName);
        if (value is null)
        {
            return default;
        }

        bool isDword = value.Kind == RegistryValueKind.DWord;
        return new AdapterCapabilities(
            Exists: true,
            IsDword: isDword,
            Value: isDword ? AsUInt32(value.Data) : null);
    }

    private static string DescribeCapabilities(AdapterCapabilities capabilities)
    {
        if (!capabilities.Exists)
        {
            return "PnPCapabilities is not set; Windows may turn this adapter off to save power.";
        }

        if (!capabilities.IsDword || capabilities.Value is null)
        {
            return "PnPCapabilities has an unexpected registry type; applying replaces it with DWORD 24.";
        }

        uint value = capabilities.Value.Value;
        return capabilities.IsRecommended
            ? $"PnPCapabilities = {value.ToString(CultureInfo.InvariantCulture)} (0x{value:X2}); " +
              "Windows cannot turn this adapter off to save power."
            : $"PnPCapabilities = {value.ToString(CultureInfo.InvariantCulture)} (0x{value:X2}); " +
              "Windows may turn this adapter off to save power.";
    }

    private static bool IsManagedEntry(RegistrySnapshotEntry entry) =>
        entry.Hive == RegistryHive.LocalMachine &&
        entry.SubKey.StartsWith(AdapterClassKey + "\\", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.ValueName, PnpCapabilitiesValueName, StringComparison.OrdinalIgnoreCase);

    private readonly record struct AdapterTarget(string SubKey, string InstanceName, string DriverDesc);

    private readonly record struct AdapterCapabilities(bool Exists, bool IsDword, uint? Value)
    {
        public bool IsRecommended =>
            IsDword && Value == RecommendedPnpCapabilities;
    }
}
