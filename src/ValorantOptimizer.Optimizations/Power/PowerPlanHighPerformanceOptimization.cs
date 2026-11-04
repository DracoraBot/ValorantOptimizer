using System.Text.RegularExpressions;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Switches the active Windows power plan to the built-in High performance scheme using
/// <c>powercfg.exe</c>. No power scheme is created or modified: Windows 11 may hide the scheme,
/// in which case the optimization reports itself as unsupported. The previous active scheme GUID
/// is stored in the <see cref="ChangeSet"/> metadata so undo can switch back to it.
/// </summary>
public sealed class PowerPlanHighPerformanceOptimization : IOptimization
{
    /// <summary>Well-known GUID of the built-in High performance scheme.</summary>
    public const string HighPerformanceSchemeId = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    private const string PowerCfgFileName = "powercfg.exe";
    private const string ListCommand = "/list";
    private const string GetActiveSchemeCommand = "/getactivescheme";
    private const string SetActiveCommand = "/setactive";
    private const string ActiveSchemeGuidMetadataKey = "activeSchemeGuid";
    private const string ActiveSchemeNameMetadataKey = "activeSchemeName";

    private static readonly Guid HighPerformanceGuid = Guid.Parse(HighPerformanceSchemeId);

    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ParenthesizedNamePattern = new(
        @"\((?<name>[^()]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = "power-plan-high-performance",
        Name = "High performance power plan",
        Category = OptimizationCategory.CpuPower,
        Risk = RiskLevel.Advanced,
        Summary = "Switch Windows to the built-in High performance power plan.",
        WhatItChanges =
            "Changes the active Windows power plan to the built-in High performance scheme " +
            "(GUID 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c). It does not create, unhide or modify any " +
            "power scheme or processor setting.",
        Why =
            "High performance keeps the processor clocked up and reduces aggressive idle/power-saving " +
            "behavior, which can make frame pacing more consistent on some desktops and plugged-in " +
            "laptops. The effect is hardware-dependent and no frame-rate gain is promised.",
        Compatibility =
            "Only when the built-in High performance scheme exists. Windows 11 may hide it; this " +
            "toolkit will not create or unhide schemes. Laptops will use more power and run warmer on battery.",
        StateWhenApplied = "High performance active",
        Restart = RestartRequirement.None,
        RequiresElevation = true,
        ManagedScopes = [],
        Notes =
        [
            "Changes processor and power behavior system-wide, not just for VALORANT.",
            "May increase power usage and heat; especially noticeable on battery.",
            "No reboot is required; the switch takes effect immediately.",
            "Windows 11 may hide the High performance plan. This toolkit does not create power schemes.",
            "Undo restores the exact scheme GUID that was active before the change, or fails clearly if no backup exists.",
        ],
    };

    public async Task<OptimizationAssessment> AssessAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        ProcessResult list = await RunPowerCfgAsync(context, [ListCommand], cancellationToken).ConfigureAwait(false);
        if (!list.Succeeded)
        {
            return OptimizationAssessment.Unknown(
                $"powercfg /list failed ({Summarize(list)}), so the available power plans could not be determined.");
        }

        IReadOnlyList<Guid> schemes = ExtractGuids(list.CombinedOutput);
        if (schemes.Count == 0)
        {
            return OptimizationAssessment.Unknown(
                "powercfg /list did not report any recognizable power plan GUIDs, so the state could not be determined.");
        }

        bool highPerformanceAvailable = schemes.Contains(HighPerformanceGuid);

        ProcessResult active = await RunPowerCfgAsync(context, [GetActiveSchemeCommand], cancellationToken)
            .ConfigureAwait(false);
        Guid activeGuid = Guid.Empty;
        string? activeName = null;
        bool activeParsed = active.Succeeded && TryParseScheme(active.CombinedOutput, out activeGuid, out activeName);

        if (!highPerformanceAvailable)
        {
            var unsupported = OptimizationAssessment.Unsupported(
                "High performance plan is not available; Windows 11 may hide it. Do not create schemes.",
                "High performance power plan is not available");
            unsupported.Evidence.Add(new AssessmentEvidence(
                "High performance plan",
                "Not listed by powercfg /list"));

            if (activeParsed)
            {
                unsupported.Evidence.Insert(0, new AssessmentEvidence(
                    "Active power plan",
                    DescribeScheme(activeGuid, activeName)));
            }

            return unsupported;
        }

        if (!active.Succeeded)
        {
            return OptimizationAssessment.Unknown(
                $"High performance plan is listed, but the active plan could not be read ({Summarize(active)}).");
        }

        if (!TryParseScheme(active.CombinedOutput, out Guid activeScheme, out string? activeSchemeName))
        {
            return OptimizationAssessment.Unknown(
                "Could not parse the active power plan GUID from powercfg /getactivescheme output.");
        }

        bool applied = activeScheme == HighPerformanceGuid;
        return new OptimizationAssessment
        {
            State = applied ? OptimizationState.Applied : OptimizationState.NotApplied,
            Headline = applied
                ? "High performance power plan is active"
                : "High performance power plan is available but not active",
            Explanation = applied
                ? "Windows is using the built-in High performance plan. This affects processor and power " +
                  "behavior system-wide; the toolkit made no other change."
                : "Applying switches the active Windows power plan to the built-in High performance plan. " +
                  "This changes processor and power behavior system-wide, may increase power use and heat, " +
                  "and does not guarantee a frame-rate gain. No reboot is required.",
            Evidence =
            [
                new AssessmentEvidence(
                    "Active power plan",
                    DescribeScheme(activeScheme, activeSchemeName)),
                new AssessmentEvidence(
                    "High performance plan",
                    $"Available ({HighPerformanceSchemeId})"),
            ],
        };
    }

    public async Task<ChangeSet> CaptureAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        ProcessResult active = await RunPowerCfgAsync(context, [GetActiveSchemeCommand], cancellationToken)
            .ConfigureAwait(false);

        if (!active.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not read the active power plan; powercfg /getactivescheme failed ({Summarize(active)}).");
        }

        if (!TryParseScheme(active.CombinedOutput, out Guid activeScheme, out string? activeSchemeName))
        {
            throw new InvalidOperationException(
                "Could not parse the active power plan GUID from powercfg /getactivescheme output; " +
                "no backup was created.");
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ActiveSchemeGuidMetadataKey] = FormatGuid(activeScheme),
        };

        if (!string.IsNullOrWhiteSpace(activeSchemeName))
        {
            metadata[ActiveSchemeNameMetadataKey] = activeSchemeName;
        }

        return new ChangeSet
        {
            OptimizationId = Descriptor.Id,
            Entries = [],
            Metadata = metadata,
        };
    }

    public async Task<OperationOutcome> ApplyAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(original);
        cancellationToken.ThrowIfCancellationRequested();

        ProcessResult setActive = await RunPowerCfgAsync(
                context,
                [SetActiveCommand, HighPerformanceSchemeId],
                cancellationToken)
            .ConfigureAwait(false);

        if (!setActive.Succeeded)
        {
            return OperationOutcome.Failed(
                $"Windows did not accept the High performance power plan command ({Summarize(setActive)}). " +
                "The active plan was left unchanged.");
        }

        ProcessResult verify = await RunPowerCfgAsync(
                context,
                [GetActiveSchemeCommand],
                cancellationToken)
            .ConfigureAwait(false);

        if (!verify.Succeeded)
        {
            return OperationOutcome.Failed(
                "The power plan command completed, but the active plan could not be verified " +
                $"({Summarize(verify)}). Use Undo to restore the previous plan.");
        }

        if (!TryParseScheme(verify.CombinedOutput, out Guid activeScheme, out string? activeSchemeName))
        {
            return OperationOutcome.Failed(
                "The power plan command completed, but the active plan GUID could not be read back. " +
                "Use Undo to restore the previous plan.");
        }

        if (activeScheme != HighPerformanceGuid)
        {
            return OperationOutcome.Failed(
                $"The active power plan is {DescribeScheme(activeScheme, activeSchemeName)} instead of " +
                "High performance; use Undo to restore the previous plan.");
        }

        var outcome = OperationOutcome.Ok(
            "Switched the active Windows power plan to High performance. This changes processor and power " +
            "behavior system-wide and may increase power use and heat; no reboot is required.",
            verified: true);
        outcome.Changes.Add($"Active power plan: {DescribeScheme(activeScheme, activeSchemeName)}");
        return outcome;
    }

    public async Task<OperationOutcome> RestoreAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(original);
        cancellationToken.ThrowIfCancellationRequested();

        if (!original.Metadata.TryGetValue(ActiveSchemeGuidMetadataKey, out string? savedGuid) ||
            string.IsNullOrWhiteSpace(savedGuid))
        {
            return OperationOutcome.Failed(
                "The saved backup does not contain the original active power plan, so the previous plan " +
                "cannot be restored automatically. Choose the desired plan manually in Windows Settings > " +
                "System > Power & battery.");
        }

        if (!Guid.TryParse(savedGuid, out Guid originalScheme))
        {
            return OperationOutcome.Failed(
                $"The saved backup contains an invalid power plan GUID ('{savedGuid}'), so the previous " +
                "plan cannot be restored automatically.");
        }

        original.Metadata.TryGetValue(ActiveSchemeNameMetadataKey, out string? originalName);

        ProcessResult setActive = await RunPowerCfgAsync(
                context,
                [SetActiveCommand, FormatGuid(originalScheme)],
                cancellationToken)
            .ConfigureAwait(false);

        if (!setActive.Succeeded)
        {
            return OperationOutcome.Failed(
                $"Could not restore the previous power plan ({Summarize(setActive)}). " +
                "Choose the desired plan manually in Windows Settings > System > Power & battery.");
        }

        ProcessResult verify = await RunPowerCfgAsync(
                context,
                [GetActiveSchemeCommand],
                cancellationToken)
            .ConfigureAwait(false);

        if (!verify.Succeeded)
        {
            return OperationOutcome.Failed(
                "The restore command completed, but the active plan could not be verified " +
                $"({Summarize(verify)}).");
        }

        if (!TryParseScheme(verify.CombinedOutput, out Guid activeScheme, out string? activeSchemeName))
        {
            return OperationOutcome.Failed(
                "The restore command completed, but the active plan GUID could not be read back.");
        }

        if (activeScheme != originalScheme)
        {
            return OperationOutcome.Failed(
                $"The previous power plan was not restored; the active plan is " +
                $"{DescribeScheme(activeScheme, activeSchemeName)}.");
        }

        var outcome = OperationOutcome.Ok(
            $"Restored the previously active power plan {DescribeScheme(originalScheme, originalName)}.",
            verified: true);
        outcome.Changes.Add($"Active power plan: {DescribeScheme(originalScheme, originalName)}");
        return outcome;
    }

    private static Task<ProcessResult> RunPowerCfgAsync(
        IOptimizationContext context,
        string[] arguments,
        CancellationToken cancellationToken) =>
        context.Processes.RunAsync(PowerCfgFileName, arguments, cancellationToken);

    private static IReadOnlyList<Guid> ExtractGuids(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var guids = new List<Guid>();
        foreach (Match match in GuidPattern.Matches(text))
        {
            if (Guid.TryParse(match.Value, out Guid guid))
            {
                guids.Add(guid);
            }
        }

        return guids;
    }

    /// <summary>
    /// Parses only the stable GUID and an optional parenthesized display name. Nothing here depends
    /// on localized words, so English, German and other powercfg outputs parse identically.
    /// </summary>
    private static bool TryParseScheme(string text, out Guid schemeGuid, out string? schemeName)
    {
        schemeGuid = Guid.Empty;
        schemeName = null;

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        Match guidMatch = GuidPattern.Match(text);
        if (!guidMatch.Success || !Guid.TryParse(guidMatch.Value, out schemeGuid))
        {
            return false;
        }

        Match nameMatch = ParenthesizedNamePattern.Match(
            text,
            guidMatch.Index + guidMatch.Length);
        if (nameMatch.Success)
        {
            string candidate = nameMatch.Groups["name"].Value.Trim();
            if (candidate.Length > 0)
            {
                schemeName = candidate;
            }
        }

        return true;
    }

    private static string DescribeScheme(Guid guid, string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? FormatGuid(guid)
            : $"{name} ({FormatGuid(guid)})";

    private static string FormatGuid(Guid guid) => guid.ToString("D");

    private static string Summarize(ProcessResult result)
    {
        string detail = result.CombinedOutput.Trim();
        if (detail.Length == 0)
        {
            return $"exit code {result.ExitCode}";
        }

        detail = Regex.Replace(detail, @"\s+", " ");
        if (detail.Length > 240)
        {
            detail = detail[..237] + "...";
        }

        return $"exit code {result.ExitCode}: {detail}";
    }
}
