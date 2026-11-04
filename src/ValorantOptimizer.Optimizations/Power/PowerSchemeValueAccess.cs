using System.Globalization;
using System.Text.RegularExpressions;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>One parsed <c>powercfg /query</c> result for a single AC/DC power setting.</summary>
public sealed record PowerSettingQuery
{
    public required Guid ActiveSchemeGuid { get; init; }

    /// <summary>Display name from the first parenthesized group, when the output has one.</summary>
    public string? ActiveSchemeName { get; init; }

    public required Guid SubgroupGuid { get; init; }

    public required Guid SettingGuid { get; init; }

    /// <summary>Current AC (plugged-in) index, parsed from the hex output.</summary>
    public required uint AcValue { get; init; }

    public uint? DcValue { get; init; }
}

public sealed record PowerSettingReadResult
{
    public required bool Success { get; init; }

    public PowerSettingQuery? Setting { get; init; }

    public string? Error { get; init; }

    public static PowerSettingReadResult Ok(PowerSettingQuery setting) => new()
    {
        Success = true,
        Setting = setting,
    };

    public static PowerSettingReadResult Failed(string error) => new()
    {
        Success = false,
        Error = error,
    };
}

public sealed record PowerSettingWriteResult
{
    public required bool Success { get; init; }

    public string? Error { get; init; }

    public static PowerSettingWriteResult Ok() => new() { Success = true };

    public static PowerSettingWriteResult Failed(string error) => new()
    {
        Success = false,
        Error = error,
    };
}

/// <summary>
/// Shared, testable access to per-scheme power settings through <c>powercfg.exe</c>. The two
/// power-setting modules use this helper so query parsing, capture metadata, apply and restore
/// behave identically.
///
/// Query values are parsed language-independently: <c>powercfg /query</c> always prints the
/// Minimum, Maximum and increment hex values first, then the current AC index and the current DC
/// index. Therefore the AC value is the second-to-last <c>0x...</c> token in the output and the DC
/// value is the last one, regardless of the localized labels around them. Fewer than two hex
/// tokens is treated as unreadable rather than guessed. A single English "Current AC" line is
/// deliberately <em>not</em> used as a fallback for safety: if the ordering convention is broken
/// the module reports Unsupported instead of writing a possibly wrong value.
/// </summary>
public static class PowerSchemeValueAccess
{
    public const string PowerCfgFileName = "powercfg.exe";
    public const string CurrentScheme = "SCHEME_CURRENT";
    public const string ActiveSchemeGuidMetadataKey = "activeSchemeGuid";
    public const string OriginalAcValueMetadataKey = "originalAcValue";

    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ParenthesizedNamePattern = new(
        @"\((?<name>[^()]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HexTokenPattern = new(
        @"0[xX](?<hex>[0-9a-fA-F]{1,8})(?![0-9a-fA-F])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Runs <c>powercfg /query SCHEME_CURRENT &lt;subgroup&gt; &lt;setting&gt;</c> and parses the
    /// current AC value. The returned subgroup/setting GUIDs must match the requested ones.
    /// </summary>
    public static async Task<PowerSettingReadResult> ReadAsync(
        IProcessRunner processes,
        string subgroupGuid,
        string settingGuid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processes);
        Guid expectedSubgroup = ParseGuidArgument(subgroupGuid, nameof(subgroupGuid));
        Guid expectedSetting = ParseGuidArgument(settingGuid, nameof(settingGuid));
        cancellationToken.ThrowIfCancellationRequested();

        ProcessResult result = await processes.RunAsync(
                PowerCfgFileName,
                ["/query", CurrentScheme, subgroupGuid, settingGuid],
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return PowerSettingReadResult.Failed(
                $"powercfg /query failed ({Summarize(result)}), so the current power setting could not be read.");
        }

        if (!TryParseQueryOutput(result.CombinedOutput, out PowerSettingQuery? setting) || setting is null)
        {
            return PowerSettingReadResult.Failed(
                "powercfg /query output did not contain a recognizable scheme GUID, subgroup GUID, setting GUID " +
                "and AC/DC hex values, so the current power setting was treated as unreadable rather than guessed.");
        }

        if (setting.SubgroupGuid != expectedSubgroup || setting.SettingGuid != expectedSetting)
        {
            return PowerSettingReadResult.Failed(
                "powercfg /query returned a different power setting than the one requested, so nothing was written.");
        }

        return PowerSettingReadResult.Ok(setting);
    }

    /// <summary>Runs <c>powercfg /setacvalueindex &lt;scheme&gt; &lt;subgroup&gt; &lt;setting&gt; &lt;value&gt;</c>.</summary>
    public static async Task<PowerSettingWriteResult> SetAcValueAsync(
        IProcessRunner processes,
        string scheme,
        string subgroupGuid,
        string settingGuid,
        uint acValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ParseGuidArgument(subgroupGuid, nameof(subgroupGuid));
        ParseGuidArgument(settingGuid, nameof(settingGuid));
        cancellationToken.ThrowIfCancellationRequested();

        ProcessResult result = await processes.RunAsync(
                PowerCfgFileName,
                [
                    "/setacvalueindex",
                    scheme,
                    subgroupGuid,
                    settingGuid,
                    acValue.ToString(CultureInfo.InvariantCulture),
                ],
                cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded
            ? PowerSettingWriteResult.Ok()
            : PowerSettingWriteResult.Failed(
                $"powercfg /setacvalueindex failed ({Summarize(result)}), so the power setting was not changed.");
    }

    /// <summary>Runs <c>powercfg /setactive &lt;scheme&gt;</c> to make the edited scheme active.</summary>
    public static async Task<PowerSettingWriteResult> ActivateSchemeAsync(
        IProcessRunner processes,
        string scheme,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        cancellationToken.ThrowIfCancellationRequested();

        ProcessResult result = await processes.RunAsync(
                PowerCfgFileName,
                ["/setactive", scheme],
                cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded
            ? PowerSettingWriteResult.Ok()
            : PowerSettingWriteResult.Failed(
                $"powercfg /setactive failed ({Summarize(result)}), so the power plan could not be reactivated.");
    }

    /// <summary>
    /// Writes the AC value and then reactivates the scheme (powercfg requires the reactivation for
    /// processors to pick up a changed value in some Windows builds).
    /// </summary>
    public static async Task<PowerSettingWriteResult> SetAcValueAndActivateAsync(
        IProcessRunner processes,
        string scheme,
        string subgroupGuid,
        string settingGuid,
        uint acValue,
        CancellationToken cancellationToken = default)
    {
        PowerSettingWriteResult set = await SetAcValueAsync(
                processes,
                scheme,
                subgroupGuid,
                settingGuid,
                acValue,
                cancellationToken)
            .ConfigureAwait(false);
        if (!set.Success)
        {
            return set;
        }

        return await ActivateSchemeAsync(processes, scheme, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses <c>powercfg /query</c> output without relying on localized labels. Returns false when
    /// the output does not contain three GUIDs or at least two hex value tokens.
    /// </summary>
    public static bool TryParseQueryOutput(string? output, out PowerSettingQuery? setting)
    {
        setting = null;
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        MatchCollection guidMatches = GuidPattern.Matches(output);
        if (guidMatches.Count < 3 ||
            !Guid.TryParse(guidMatches[0].Value, out Guid schemeGuid) ||
            !Guid.TryParse(guidMatches[1].Value, out Guid subgroupGuid) ||
            !Guid.TryParse(guidMatches[2].Value, out Guid settingGuid))
        {
            return false;
        }

        string? schemeName = null;
        int nameSearchStart = guidMatches[0].Index + guidMatches[0].Length;
        int lineEnd = output.IndexOfAny(['\r', '\n'], nameSearchStart);
        string schemeLineRemainder = lineEnd < 0
            ? output[nameSearchStart..]
            : output[nameSearchStart..lineEnd];
        Match nameMatch = ParenthesizedNamePattern.Match(schemeLineRemainder);
        if (nameMatch.Success)
        {
            string candidate = nameMatch.Groups["name"].Value.Trim();
            if (candidate.Length > 0)
            {
                schemeName = candidate;
            }
        }

        var hexValues = new List<uint>();
        foreach (Match match in HexTokenPattern.Matches(output))
        {
            if (uint.TryParse(
                    match.Groups["hex"].Value,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out uint parsed))
            {
                hexValues.Add(parsed);
            }
        }

        // powercfg always prints: minimum, maximum, increment, current AC, current DC.
        // The two current indexes are the final two hex tokens in that fixed order.
        if (hexValues.Count < 2)
        {
            return false;
        }

        setting = new PowerSettingQuery
        {
            ActiveSchemeGuid = schemeGuid,
            ActiveSchemeName = schemeName,
            SubgroupGuid = subgroupGuid,
            SettingGuid = settingGuid,
            AcValue = hexValues[^2],
            DcValue = hexValues[^1],
        };
        return true;
    }

    /// <summary>Builds the backup metadata for a captured setting (canonical decimal AC value).</summary>
    public static Dictionary<string, string> CreateMetadata(PowerSettingQuery setting)
    {
        ArgumentNullException.ThrowIfNull(setting);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ActiveSchemeGuidMetadataKey] = setting.ActiveSchemeGuid.ToString("D"),
            [OriginalAcValueMetadataKey] = setting.AcValue.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// Validates and reads backup metadata. Never guesses: missing or malformed values produce a
    /// clear error so the caller must fail the restore instead of writing something else.
    /// </summary>
    public static bool TryReadMetadata(
        ChangeSet changeSet,
        out Guid activeSchemeGuid,
        out uint originalAcValue,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(changeSet);

        activeSchemeGuid = Guid.Empty;
        originalAcValue = 0;
        error = null;

        if (!changeSet.Metadata.TryGetValue(ActiveSchemeGuidMetadataKey, out string? rawGuid) ||
            string.IsNullOrWhiteSpace(rawGuid))
        {
            error = "The saved backup does not contain the original power plan GUID, so this power " +
                    "setting cannot be restored automatically.";
            return false;
        }

        if (!Guid.TryParse(rawGuid, out activeSchemeGuid) || activeSchemeGuid == Guid.Empty)
        {
            error = $"The saved backup contains an invalid power plan GUID ('{rawGuid}'), so this power " +
                    "setting cannot be restored automatically.";
            return false;
        }

        if (!changeSet.Metadata.TryGetValue(OriginalAcValueMetadataKey, out string? rawValue) ||
            string.IsNullOrWhiteSpace(rawValue))
        {
            error = "The saved backup does not contain the original AC value, so this power setting " +
                    "cannot be restored automatically.";
            return false;
        }

        if (!TryParseAcValue(rawValue, out originalAcValue))
        {
            error = $"The saved backup contains an invalid original AC value ('{rawValue}'), so this " +
                    "power setting cannot be restored automatically.";
            return false;
        }

        return true;
    }

    public static string FormatGuid(Guid guid) => guid.ToString("D");

    /// <summary>Short single-line description of a powercfg result for messages and logs.</summary>
    public static string Summarize(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

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

    private static bool TryParseAcValue(string raw, out uint value)
    {
        // Canonical metadata stores decimal, but tolerate a hex string from a hand-edited backup.
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(
                raw[2..],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out value);
        }

        return uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static Guid ParseGuidArgument(string value, string parameterName)
    {
        if (!Guid.TryParse(value, out Guid guid) || guid == Guid.Empty)
        {
            throw new ArgumentException($"'{value}' is not a valid GUID.", parameterName);
        }

        return guid;
    }
}
