using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Read-only network overview. It reports stored DNS servers and the number of TCP/IP interface
/// configurations, but never changes DNS, adapters, the hosts file or any network policy.
/// </summary>
public sealed class NetworkOverviewReview : IOptimization
{
    public const string OptimizationId = "network-overview";

    public const string InterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    public const string NameServerValueName = "NameServer";
    public const string DhcpNameServerValueName = "DhcpNameServer";

    private const int MaximumDnsEvidenceEntries = 5;

    private const string BlockedReason =
        "DNS changes are environment-specific: the best servers depend on your router, ISP, VPN, " +
        "content filters and enterprise policy. This toolkit does not override DNS automatically. " +
        "Change DNS deliberately in Windows Settings or on your router if you decide to.";

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = OptimizationId,
        Name = "Network / DNS overview (review)",
        Category = OptimizationCategory.Network,
        Risk = RiskLevel.Safe,
        Summary = "Summarize stored DNS servers and TCP/IP interface counts; DNS choices stay environment-specific.",
        WhatItChanges =
            @"Nothing. Reads NameServer and DhcpNameServer from the stored interfaces under " +
            @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces and counts interface " +
            "configurations. No DNS server, adapter, route, hosts-file entry or policy is changed.",
        Why =
            "DNS resolver choice affects name lookup latency and reliability, but the right servers " +
            "depend on your network: a work VPN may require internal resolvers, a content filter may be " +
            "intentional, and a fast public resolver can be slower through some ISPs. The card shows what " +
            "Windows has stored so you can make an informed choice; it does not apply a blanket DNS " +
            "override. No latency or frame-rate improvement is promised.",
        Compatibility =
            "Windows 10 and Windows 11. Values come from the stored TCP/IP interface configuration; " +
            "VPNs and filter drivers can register additional interfaces, and IPv6 DNS configuration is " +
            "not included in this summary.",
        StateWhenApplied = "Read-only review",
        Restart = RestartRequirement.None,
        RequiresElevation = false,
        ManagedScopes = [],
        Notes =
        [
            "Read-only: apply and restore are refused by design, and the card is disabled in the UI.",
            "No DNS override, hosts-file edit or adapter setting is performed.",
            "VPNs, routers and enterprise policy may supply different DNS servers than the stored values shown.",
            "An empty static NameServer usually means DNS comes from DHCP or the router.",
        ],
    };

    public Task<OptimizationAssessment> AssessAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<string>? interfaceIds = ReviewOnlySupport.TryGetSubKeyNames(
            context.Registry,
            RegistryHive.LocalMachine,
            InterfacesKey);

        if (interfaceIds is null)
        {
            OptimizationAssessment unavailable = OptimizationAssessment.Review(
                BlockedReason,
                "Network overview is unavailable") with
            {
                Explanation =
                    "The TCP/IP interface registry could not be read, so the card does not guess at DNS " +
                    "or adapter counts. Nothing is changed by this card.",
                Evidence =
                [
                    new AssessmentEvidence("Interfaces", "Unavailable"),
                    new AssessmentEvidence("DNS", "Unavailable"),
                ],
            };
            return Task.FromResult(unavailable);
        }

        var configured = new List<InterfaceDns>();
        foreach (string interfaceId in interfaceIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            string key = InterfacesKey + "\\" + interfaceId;
            string? nameServer = ReviewOnlySupport.TryReadText(
                context.Registry,
                RegistryHive.LocalMachine,
                key,
                NameServerValueName);
            string? dhcpNameServer = ReviewOnlySupport.TryReadText(
                context.Registry,
                RegistryHive.LocalMachine,
                key,
                DhcpNameServerValueName);

            if (nameServer is not null || dhcpNameServer is not null)
            {
                configured.Add(new InterfaceDns(interfaceId, nameServer, dhcpNameServer));
            }
        }

        var evidence = new List<AssessmentEvidence>
        {
            new("Interfaces", $"{interfaceIds.Count} stored Tcpip interface configuration{(interfaceIds.Count == 1 ? string.Empty : "s")}"),
            new("Interfaces with DNS", $"{configured.Count} of {interfaceIds.Count}"),
        };

        foreach (InterfaceDns interfaceDns in configured.Take(MaximumDnsEvidenceEntries))
        {
            evidence.Add(new AssessmentEvidence(
                $"DNS {interfaceDns.InterfaceId}",
                DescribeDns(interfaceDns)));
        }

        if (configured.Count == 0)
        {
            evidence.Add(new AssessmentEvidence(
                "DNS",
                "No per-interface DNS servers stored; Windows likely uses DHCP/router DNS."));
        }
        else if (configured.Count > MaximumDnsEvidenceEntries)
        {
            evidence.Add(new AssessmentEvidence(
                "More interfaces",
                $"{configured.Count - MaximumDnsEvidenceEntries} additional interface(s) not listed"));
        }

        string headline =
            $"Network overview: {interfaceIds.Count} interface configuration{(interfaceIds.Count == 1 ? string.Empty : "s")}, " +
            $"{configured.Count} with stored DNS";

        OptimizationAssessment assessment = OptimizationAssessment.Review(BlockedReason, headline) with
        {
            Explanation =
                "The DNS servers above are what Windows stores for each interface. They may have come " +
                "from DHCP, a VPN, a router or a manual setting; this card does not change them. If name " +
                "lookups feel slow, compare the stored values with what your network actually expects " +
                "before changing DNS. Nothing is changed by this card.",
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

        IReadOnlyList<string>? interfaceIds = ReviewOnlySupport.TryGetSubKeyNames(
            context.Registry,
            RegistryHive.LocalMachine,
            InterfacesKey);
        interfaceIds ??= [];

        var dnsEntries = new List<string>();
        foreach (string interfaceId in interfaceIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            string key = InterfacesKey + "\\" + interfaceId;
            string? nameServer = ReviewOnlySupport.TryReadText(
                context.Registry,
                RegistryHive.LocalMachine,
                key,
                NameServerValueName);
            string? dhcpNameServer = ReviewOnlySupport.TryReadText(
                context.Registry,
                RegistryHive.LocalMachine,
                key,
                DhcpNameServerValueName);

            if (nameServer is not null || dhcpNameServer is not null)
            {
                dnsEntries.Add(
                    $"{interfaceId}: nameServer={(nameServer ?? "none")}; dhcpNameServer={(dhcpNameServer ?? "none")}");
            }
        }

        ChangeSet changeSet = ReviewOnlySupport.CreateChangeSet(
            Descriptor.Id,
            ("interfaceCount", ReviewOnlySupport.Invariant(interfaceIds.Count)),
            ("dnsInterfaceCount", ReviewOnlySupport.Invariant(dnsEntries.Count)),
            ("dnsServers", dnsEntries.Count == 0 ? "none" : string.Join(" | ", dnsEntries)));

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

    private static string DescribeDns(InterfaceDns interfaceDns)
    {
        if (interfaceDns.NameServer is not null && interfaceDns.DhcpNameServer is not null)
        {
            return $"{interfaceDns.NameServer} (static); DHCP: {interfaceDns.DhcpNameServer}";
        }

        return interfaceDns.NameServer is not null
            ? $"{interfaceDns.NameServer} (static)"
            : $"{interfaceDns.DhcpNameServer} (DHCP)";
    }

    private sealed record InterfaceDns(string InterfaceId, string? NameServer, string? DhcpNameServer);
}
