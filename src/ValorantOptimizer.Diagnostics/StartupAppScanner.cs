using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Diagnostics;

/// <summary>Flag values attached to <see cref="StartupAppEntry.Flags"/>.</summary>
public static class StartupAppFlags
{
    public const string AntiCheat = "Anti-cheat";
    public const string RiotGames = "Riot Games";
    public const string Valorant = "VALORANT";
    public const string Nvidia = "NVIDIA";
    public const string Amd = "AMD";
    public const string Intel = "Intel";
    public const string Audio = "Audio";
    public const string OneDrive = "OneDrive";
    public const string Discord = "Discord";
    public const string Steam = "Steam";
    public const string SystemComponent = "System component";
}

/// <summary>
/// Read-only startup application inventory. Enumerates the HKCU/HKLM Run keys, their
/// StartupApproved state values and the per-user/per-machine Startup folders. This class never
/// writes, deletes or toggles anything: the UI only reviews the list and the user decides what to
/// disable through Windows itself.
/// </summary>
public sealed class StartupAppScanner : IStartupAppScanner
{
    public const string ReviewOnlyNote =
        "Review only — the toolkit never enables, disables, or deletes startup entries.";

    internal const string UserRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string MachineRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    internal const string UserApprovedRunKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    internal const string UserApprovedRun32Key =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    internal const string MachineApprovedRunKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    internal const string MachineApprovedRun32Key =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    internal const string UserApprovedStartupFolderKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    internal const string MachineApprovedStartupFolderKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    private readonly IRegistryStore _registry;
    private readonly string? _userStartupFolder;
    private readonly string? _machineStartupFolder;
    private readonly Func<string, string?>? _publisherResolver;

    /// <param name="registry">Registry reader (faked in tests).</param>
    /// <param name="userStartupFolder">Override for the per-user Startup folder (tests).</param>
    /// <param name="machineStartupFolder">Override for the all-users Startup folder (tests).</param>
    /// <param name="publisherResolver">Override for publisher resolution (tests).</param>
    public StartupAppScanner(
        IRegistryStore registry,
        string? userStartupFolder = null,
        string? machineStartupFolder = null,
        Func<string, string?>? publisherResolver = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _registry = registry;
        _userStartupFolder = userStartupFolder;
        _machineStartupFolder = machineStartupFolder;
        _publisherResolver = publisherResolver;
    }

    public Task<IReadOnlyList<StartupAppEntry>> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = new List<StartupAppEntry>();
        try
        {
            CollectRunEntries(RegistryHive.CurrentUser, UserRunKey, StartupSource.UserRunKey, entries, cancellationToken);
            CollectRunEntries(RegistryHive.LocalMachine, MachineRunKey, StartupSource.MachineRunKey, entries, cancellationToken);

            CollectStartupFolderEntries(
                _userStartupFolder ?? GetUserStartupFolder(),
                StartupSource.UserStartupFolder,
                RegistryHive.CurrentUser,
                entries,
                cancellationToken);

            CollectStartupFolderEntries(
                _machineStartupFolder ?? GetMachineStartupFolder(),
                StartupSource.MachineStartupFolder,
                RegistryHive.LocalMachine,
                entries,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Every source already isolates its own failures; this is only a last-resort guard so
            // a review screen can still render the entries collected so far.
        }

        return Task.FromResult<IReadOnlyList<StartupAppEntry>>(Sort(entries));
    }

    // ---------- Run keys ----------

    private void CollectRunEntries(
        RegistryHive hive,
        string runKey,
        StartupSource source,
        List<StartupAppEntry> entries,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> valueNames;
        try
        {
            valueNames = _registry.GetValueNames(hive, runKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        foreach (string valueName in valueNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                RegistryValue? value = _registry.ReadValue(hive, runKey, valueName);
                if (value is null)
                {
                    continue;
                }

                string rawCommand = Convert.ToString(value.Data, CultureInfo.InvariantCulture) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawCommand))
                {
                    continue;
                }

                string command = value.Kind == RegistryValueKind.ExpandString
                    ? Environment.ExpandEnvironmentVariables(rawCommand)
                    : rawCommand;

                bool isEnabled = FindStartupApprovedEnabled(hive, machine: hive == RegistryHive.LocalMachine, valueName)
                                 ?? true;

                entries.Add(CreateEntry(valueName, command, source, isEnabled));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreadable value must not hide the rest of the startup list.
            }
        }
    }

    /// <summary>
    /// Looks up the matching StartupApproved value. Machine entries check ...\Run first and then
    /// ...\Run32 (the WOW64 view of the same key); user entries check ...\Run then ...\Run32.
    /// Returns null when no approval decision exists.
    /// </summary>
    private bool? FindStartupApprovedEnabled(RegistryHive hive, bool machine, string valueName)
    {
        string[] approvedKeys = machine
            ? [MachineApprovedRunKey, MachineApprovedRun32Key]
            : [UserApprovedRunKey, UserApprovedRun32Key];

        foreach (string key in approvedKeys)
        {
            bool? state = ParseStartupApprovedEnabled(SafeReadValue(hive, key, valueName));
            if (state is not null)
            {
                return state;
            }
        }

        return null;
    }

    // ---------- Startup folders ----------

    private void CollectStartupFolderEntries(
        string? folder,
        StartupSource source,
        RegistryHive approvalHive,
        List<StartupAppEntry> entries,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        string[] files;
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            files = Directory.GetFiles(folder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fileName = Path.GetFileName(file);
            if (string.IsNullOrEmpty(fileName) ||
                string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool isEnabled = FindStartupFolderApprovedEnabled(approvalHive, fileName) ?? true;
            string name = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = fileName;
            }

            entries.Add(CreateEntry(name, file, source, isEnabled));
        }
    }

    private bool? FindStartupFolderApprovedEnabled(RegistryHive hive, string fileName)
    {
        string key = hive == RegistryHive.LocalMachine
            ? MachineApprovedStartupFolderKey
            : UserApprovedStartupFolderKey;

        bool? state = ParseStartupApprovedEnabled(SafeReadValue(hive, key, fileName));
        if (state is not null)
        {
            return state;
        }

        return ParseStartupApprovedEnabled(
            SafeReadValue(hive, key, Path.GetFileNameWithoutExtension(fileName)));
    }

    // ---------- StartupApproved decoding ----------

    internal enum StartupApprovalState
    {
        NotSpecified = 0,
        Enabled,
        Disabled,
    }

    /// <summary>
    /// Decodes the first byte of a StartupApproved REG_BINARY value. Exact table used here:
    /// <list type="bullet">
    /// <item><description><c>02 00 00 00 ...</c> = enabled.</description></item>
    /// <item><description><c>03 00 00 00 ...</c> = disabled (remaining bytes usually hold a FILETIME).</description></item>
    /// <item><description><c>06 00 00 00 ...</c> = enabled (policy/alternate flag).</description></item>
    /// <item><description><c>07 00 00 00 ...</c> = disabled (policy/alternate flag).</description></item>
    /// <item><description>Any other value, a missing value, or a non-binary value = no decision,
    /// which is treated as enabled.</description></item>
    /// </list>
    /// </summary>
    internal static StartupApprovalState ParseStartupApproved(RegistryValue? value)
    {
        if (value is null ||
            value.Kind != RegistryValueKind.Binary ||
            value.Data is not byte[] { Length: > 0 } data)
        {
            return StartupApprovalState.NotSpecified;
        }

        return data[0] switch
        {
            0x02 or 0x06 => StartupApprovalState.Enabled,
            0x03 or 0x07 => StartupApprovalState.Disabled,
            _ => StartupApprovalState.NotSpecified,
        };
    }

    /// <summary>Null means "no decision"; only an explicit disabled marker returns false.</summary>
    internal static bool? ParseStartupApprovedEnabled(RegistryValue? value) =>
        ParseStartupApproved(value) switch
        {
            StartupApprovalState.Enabled => true,
            StartupApprovalState.Disabled => false,
            _ => null,
        };

    // ---------- Entry construction ----------

    private StartupAppEntry CreateEntry(
        string name,
        string command,
        StartupSource source,
        bool isEnabled)
    {
        string? publisher = ResolvePublisher(command);
        List<string> flags = DetectFlags(name, command, publisher);

        string note = ReviewOnlyNote;
        if (flags.Contains(StartupAppFlags.AntiCheat))
        {
            note += " Riot Vanguard must run alongside VALORANT; do not disable it.";
        }
        else if (flags.Contains(StartupAppFlags.Nvidia) ||
                 flags.Contains(StartupAppFlags.Amd) ||
                 flags.Contains(StartupAppFlags.Intel))
        {
            note += " Disabling a graphics driver helper can affect display behavior.";
        }

        return new StartupAppEntry
        {
            Name = name,
            Command = command,
            Source = source,
            IsEnabled = isEnabled,
            Publisher = publisher,
            Flags = flags,
            Note = note,
        };
    }

    private string? ResolvePublisher(string command)
    {
        if (_publisherResolver is not null)
        {
            try
            {
                return NullIfBlank(_publisherResolver(command));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        string? executablePath = TryExtractExecutablePath(command);
        if (executablePath is null)
        {
            return null;
        }

        try
        {
            if (!File.Exists(executablePath))
            {
                return null;
            }

            return NullIfBlank(FileVersionInfo.GetVersionInfo(executablePath).CompanyName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the executable path from a Run command. Handles quoted paths, unquoted paths with
    /// spaces, environment variables and trailing arguments. Null when no .exe is present.
    /// </summary>
    internal static string? TryExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string expanded = Environment.ExpandEnvironmentVariables(command).Trim();
        if (expanded.Length == 0)
        {
            return null;
        }

        if (expanded.StartsWith('"'))
        {
            int close = expanded.IndexOf('"', 1);
            if (close > 1)
            {
                return expanded[1..close];
            }

            return null;
        }

        int exeIndex = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex <= 0)
        {
            return null;
        }

        return expanded[..(exeIndex + 4)].Trim();
    }

    /// <summary>
    /// Identity annotations based on the entry name, command line and publisher. Matching is
    /// deliberately conservative: only well-known vendors/process helpers are flagged.
    /// </summary>
    internal static List<string> DetectFlags(string name, string command, string? publisher)
    {
        string haystack = string.Concat(name, " ", command, " ", publisher ?? string.Empty);
        var flags = new List<string>();

        if (ContainsAny(haystack, "vanguard", "vgc", "vgk"))
        {
            AddFlag(flags, StartupAppFlags.AntiCheat);
        }

        if (ContainsAny(haystack, "riot ", @"riot\", "riot games", "riot client"))
        {
            AddFlag(flags, StartupAppFlags.RiotGames);
        }

        if (haystack.Contains("valorant", StringComparison.OrdinalIgnoreCase))
        {
            AddFlag(flags, StartupAppFlags.Valorant);
        }

        if (ContainsAny(haystack, "nvidia", "geforce", "nvtray", "nvcontainer", "nvsphelper"))
        {
            AddFlag(flags, StartupAppFlags.Nvidia);
        }

        // Deliberately not a bare "amd": "%SystemRoot%\...\realtekservice.inf_amd64_..." is a very
        // common driver-store path and "amd64" must not label a Realtek device as AMD hardware.
        if (ContainsAny(haystack, "amd ", @"amd\", "amd.", "advanced micro devices", "radeon"))
        {
            AddFlag(flags, StartupAppFlags.Amd);
        }

        if (ContainsAny(haystack, "intel ", @"intel\", "intel(", "igfx"))
        {
            AddFlag(flags, StartupAppFlags.Intel);
        }

        if (ContainsAny(haystack, "realtek", "rtk", "nahimic", "dolby", "voicemeeter", "audio"))
        {
            AddFlag(flags, StartupAppFlags.Audio);
        }

        if (haystack.Contains("onedrive", StringComparison.OrdinalIgnoreCase))
        {
            AddFlag(flags, StartupAppFlags.OneDrive);
        }

        if (haystack.Contains("discord", StringComparison.OrdinalIgnoreCase))
        {
            AddFlag(flags, StartupAppFlags.Discord);
        }

        if (haystack.Contains("steam", StringComparison.OrdinalIgnoreCase))
        {
            AddFlag(flags, StartupAppFlags.Steam);
        }

        if (ContainsAny(haystack, "securityhealth", "windows security", "defender"))
        {
            AddFlag(flags, StartupAppFlags.SystemComponent);
        }

        return flags;
    }

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(needle => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static void AddFlag(List<string> flags, string flag)
    {
        if (!flags.Contains(flag, StringComparer.Ordinal))
        {
            flags.Add(flag);
        }
    }

    // ---------- Helpers ----------

    private RegistryValue? SafeReadValue(RegistryHive hive, string subKey, string valueName)
    {
        try
        {
            return _registry.ReadValue(hive, subKey, valueName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static List<StartupAppEntry> Sort(List<StartupAppEntry> entries) =>
        entries
            .OrderByDescending(entry => entry.IsEnabled)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Source)
            .ThenBy(entry => entry.Command, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? GetUserStartupFolder()
    {
        string path = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static string? GetMachineStartupFolder()
    {
        string path = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
