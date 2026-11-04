using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Diagnostics;

/// <summary>
/// Locates the VALORANT game client without administrator rights. Detection is read-only and
/// ordered: Riot's product settings metadata first, then running processes, then uninstall
/// registry entries, then well-known install folders. Every source is best effort; a failure is
/// recorded in <see cref="ValorantInstallation.Notes"/> instead of throwing.
/// </summary>
public sealed class ValorantLocator : IValorantLocator
{
    public const string DetectionSourceProductSettings = "Riot product settings";
    public const string DetectionSourceRunningProcess = "Running process";
    public const string DetectionSourceUninstallRegistry = "Uninstall registry";
    public const string DetectionSourceCommonInstallRoot = "Common install folder";

    internal const string ProductSettingsRelativePath =
        @"Riot Games\Metadata\valorant.live\valorant.live.product_settings.json";

    internal const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    internal const string UninstallKey32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    internal static readonly string[] ProcessNames = ["VALORANT", "VALORANT-Win64-Shipping"];

    private readonly IRegistryStore _registry;
    private readonly string? _programDataPath;
    private readonly IReadOnlyList<string>? _installRoots;
    private readonly Func<IReadOnlyList<string>>? _runningExecutablePaths;

    /// <summary>
    /// Creates a locator. <paramref name="processes"/> is part of the shared diagnostics contract
    /// (same dependency shape as the optimization context) and is accepted for constructor
    /// symmetry; running process paths are read through <see cref="Process"/> because
    /// <see cref="IProcessRunner"/> only launches tools. The remaining parameters are test seams.
    /// </summary>
    public ValorantLocator(
        IRegistryStore registry,
        IProcessRunner processes,
        string? programDataPath = null,
        IEnumerable<string>? installRoots = null,
        Func<IReadOnlyList<string>>? runningExecutablePaths = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(processes);

        _registry = registry;
        _programDataPath = programDataPath;
        _installRoots = installRoots?.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        _runningExecutablePaths = runningExecutablePaths;
    }

    public Task<ValorantInstallation> LocateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(Locate(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ValorantInstallation
            {
                Notes = [$"VALORANT detection failed unexpectedly: {ex.Message}"],
            });
        }
    }

    private ValorantInstallation Locate(CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        string programData = _programDataPath ?? GetDefaultProgramData();
        IReadOnlyList<string> installRoots = _installRoots ?? GetDefaultInstallRoots();

        AddRiotClientEvidence(programData, notes);

        CandidateResult? productSettingsFallback = null;
        ProductSettingsEvidence? settings = TryReadProductSettings(
            Path.Combine(programData, ProductSettingsRelativePath),
            notes);

        if (settings is not null)
        {
            foreach (string source in BuildProductSettingsSources(settings, installRoots, notes))
            {
                cancellationToken.ThrowIfCancellationRequested();

                CandidateResult? verified = BuildCandidate(source, acceptMissingFile: false);
                if (verified is not null)
                {
                    return BuildInstallation(verified, DetectionSourceProductSettings, notes);
                }

                productSettingsFallback ??= BuildCandidate(source, acceptMissingFile: true);
            }

            if (productSettingsFallback is null)
            {
                notes.Add("Riot product settings did not produce a usable VALORANT.exe path.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (string runningPath in GetRunningExecutablePaths(notes))
        {
            CandidateResult? verified = BuildCandidate(runningPath, acceptMissingFile: false);
            if (verified is not null)
            {
                return BuildInstallation(verified, DetectionSourceRunningProcess, notes);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        bool uninstallMatched = false;
        foreach (string uninstallKey in new[] { UninstallKey, UninstallKey32 })
        {
            CandidateResult? candidate = TryResolveUninstallEntry(uninstallKey, notes, out bool matched);
            uninstallMatched |= matched;
            if (candidate is not null)
            {
                return BuildInstallation(candidate, DetectionSourceUninstallRegistry, notes);
            }
        }

        if (!uninstallMatched)
        {
            notes.Add(@"No VALORANT uninstall entry was found in HKLM or WOW6432Node.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (string root in installRoots)
        {
            CandidateResult? verified = BuildCandidate(root, acceptMissingFile: false);
            if (verified is not null)
            {
                return BuildInstallation(verified, DetectionSourceCommonInstallRoot, notes);
            }
        }

        notes.Add(installRoots.Count == 0
            ? "No common VALORANT install folders were available to search."
            : $"No VALORANT.exe was found in {installRoots.Count} common install folder(s).");

        if (productSettingsFallback is not null)
        {
            return BuildInstallation(productSettingsFallback, DetectionSourceProductSettings, notes);
        }

        return new ValorantInstallation { Notes = [.. notes] };
    }

    // ---------- Product settings (primary source) ----------

    private static ProductSettingsEvidence? TryReadProductSettings(string productSettingsPath, List<string> notes)
    {
        try
        {
            if (!File.Exists(productSettingsPath))
            {
                notes.Add($"Riot product settings file was not found at '{productSettingsPath}'.");
                return null;
            }

            string json = File.ReadAllText(productSettingsPath);
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                notes.Add("Riot product settings file did not contain a JSON object.");
                return null;
            }

            string? fullPath = ReadJsonString(document.RootElement, "product_install_full_path");
            string? partialPath = ReadJsonString(document.RootElement, "product_install_partial_path");

            if (fullPath is null && partialPath is null)
            {
                notes.Add("Riot product settings file did not contain install path fields.");
                return null;
            }

            notes.Add("Riot product settings metadata was readable.");
            return new ProductSettingsEvidence(fullPath, partialPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            notes.Add($"Could not read Riot product settings: {ex.Message}");
            return null;
        }
    }

    private static string? ReadJsonString(JsonElement element, string propertyName)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? value = property.Value.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        return null;
    }

    private static IReadOnlyList<string> BuildProductSettingsSources(
        ProductSettingsEvidence settings,
        IReadOnlyList<string> installRoots,
        List<string> notes)
    {
        var sources = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.FullPath))
        {
            sources.Add(settings.FullPath);
        }

        if (!string.IsNullOrWhiteSpace(settings.PartialPath))
        {
            IReadOnlyList<string> expanded = ExpandPartialInstallPath(settings.PartialPath, installRoots);
            if (expanded.Count == 0)
            {
                notes.Add(
                    $"Riot product settings partial path '{settings.PartialPath}' could not be resolved against common install folders.");
            }

            sources.AddRange(expanded);
        }

        return sources;
    }

    /// <summary>
    /// Expands Riot's partial install path (for example "VALORANT/live") against the parent of
    /// each known install folder (for example "C:\Riot Games") and the folder itself.
    /// </summary>
    internal static IReadOnlyList<string> ExpandPartialInstallPath(
        string? partialPath,
        IReadOnlyList<string> installRoots)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(partialPath))
        {
            return results;
        }

        string normalized = partialPath.Trim().Replace('/', Path.DirectorySeparatorChar).Trim('\\', '/');
        if (normalized.Length == 0)
        {
            return results;
        }

        if (Path.IsPathRooted(normalized))
        {
            results.Add(normalized);
            return results;
        }

        foreach (string root in installRoots)
        {
            string trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? parent = Path.GetDirectoryName(trimmedRoot);
            if (!string.IsNullOrEmpty(parent))
            {
                results.Add(Path.Combine(parent, normalized));
            }

            results.Add(Path.Combine(trimmedRoot, normalized));
        }

        return Distinct(results);
    }

    // ---------- Running processes ----------

    private IReadOnlyList<string> GetRunningExecutablePaths(List<string> notes)
    {
        if (_runningExecutablePaths is not null)
        {
            try
            {
                return _runningExecutablePaths()
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add($"Running process path probe failed: {ex.Message}");
                return [];
            }
        }

        var paths = new List<string>();
        bool sawProcess = false;
        bool pathDenied = false;

        foreach (string processName in ProcessNames)
        {
            try
            {
                foreach (Process process in Process.GetProcessesByName(processName))
                {
                    sawProcess = true;
                    try
                    {
                        string? path = process.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            pathDenied = true;
                        }
                        else
                        {
                            paths.Add(path);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Vanguard protects game processes; the path is often unreadable.
                        pathDenied = true;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add($"Could not enumerate '{processName}' processes: {ex.Message}");
            }
        }

        if (sawProcess && paths.Count == 0 && pathDenied)
        {
            notes.Add("VALORANT is running but its executable path is not readable.");
        }
        else if (!sawProcess)
        {
            notes.Add("VALORANT is not currently running.");
        }

        return paths;
    }

    // ---------- Uninstall registry ----------

    private CandidateResult? TryResolveUninstallEntry(
        string uninstallKey,
        List<string> notes,
        out bool matched)
    {
        matched = false;

        IReadOnlyList<string> subKeys;
        try
        {
            subKeys = _registry.GetSubKeyNames(RegistryHive.LocalMachine, uninstallKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            notes.Add($@"Uninstall registry enumeration failed for HKLM\{uninstallKey}: {ex.Message}");
            return null;
        }

        foreach (string subKey in subKeys)
        {
            string fullKey = uninstallKey + "\\" + subKey;
            string? displayName = ReadRegistryString(fullKey, "DisplayName");
            if (displayName is null || !displayName.Contains("VALORANT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matched = true;
            notes.Add($@"Uninstall entry '{displayName}' matched under HKLM\{uninstallKey}.");

            var sources = new List<string>();
            string? displayIcon = ReadRegistryString(fullKey, "DisplayIcon");
            string? installLocation = ReadRegistryString(fullKey, "InstallLocation");
            if (!string.IsNullOrWhiteSpace(displayIcon))
            {
                sources.Add(displayIcon);
            }

            if (!string.IsNullOrWhiteSpace(installLocation))
            {
                sources.Add(installLocation);
            }

            foreach (string source in sources)
            {
                CandidateResult? candidate = BuildCandidate(source, acceptMissingFile: false);
                if (candidate is not null)
                {
                    return candidate;
                }
            }

            // Riot's uninstall icon sometimes points at RiotClientServices.exe; derive the sibling
            // VALORANT folder under the same Riot Games root.
            foreach (string source in sources)
            {
                string? siblingRoot = TryDeriveRiotGameRoot(source);
                if (siblingRoot is null)
                {
                    continue;
                }

                CandidateResult? candidate = BuildCandidate(siblingRoot, acceptMissingFile: false);
                if (candidate is not null)
                {
                    return candidate;
                }
            }

            notes.Add($"Uninstall entry '{displayName}' did not point at a readable VALORANT.exe.");
        }

        return null;
    }

    private string? ReadRegistryString(string subKey, string valueName)
    {
        try
        {
            RegistryValue? value = _registry.ReadValue(RegistryHive.LocalMachine, subKey, valueName);
            return value?.Data switch
            {
                string text => text,
                null => null,
                object other => other.ToString(),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Given a Riot Client path, returns the sibling "VALORANT" folder when recognizable.</summary>
    internal static string? TryDeriveRiotGameRoot(string? evidencePath)
    {
        string? cleaned = CleanEvidencePath(evidencePath);
        if (cleaned is null || !Path.IsPathRooted(cleaned))
        {
            return null;
        }

        string? directory = SafeFileExists(cleaned) ? Path.GetDirectoryName(cleaned) : cleaned;
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        string trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetFileName(trimmed), "Riot Client", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? parent = Path.GetDirectoryName(trimmed);
        return string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, "VALORANT");
    }

    // ---------- Candidate path derivation ----------

    /// <summary>
    /// Derives candidate VALORANT.exe paths from a directory, an executable path or an install
    /// location. The launcher may live directly in the install folder or in its "live" child.
    /// </summary>
    internal static IReadOnlyList<string> BuildExecutableCandidates(string? rawPath)
    {
        var results = new List<string>();
        string? cleaned = CleanEvidencePath(rawPath);
        if (cleaned is null)
        {
            return results;
        }

        if (string.Equals(Path.GetFileName(cleaned), "VALORANT.exe", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(cleaned);
        }

        string? directory = SafeFileExists(cleaned) ? Path.GetDirectoryName(cleaned) : cleaned;
        if (directory is not null)
        {
            AddDirectoryCandidates(directory, results);
            AddLiveDirectoryCandidatesFromBinariesPath(directory, results);
        }

        return Distinct(results);
    }

    /// <summary>Derives the shipping binary path(s) for a VALORANT install folder or executable.</summary>
    internal static IReadOnlyList<string> BuildShippingCandidates(string? rawPath)
    {
        var results = new List<string>();
        string? cleaned = CleanEvidencePath(rawPath);
        if (cleaned is null)
        {
            return results;
        }

        string? directory = SafeFileExists(cleaned) ? Path.GetDirectoryName(cleaned) : cleaned;
        if (string.IsNullOrEmpty(directory))
        {
            return results;
        }

        string? current = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int depth = 0; depth < 6 && !string.IsNullOrEmpty(current); depth++)
        {
            AddShippingCandidatesForDirectory(current, results);
            current = Path.GetDirectoryName(current);
        }

        return Distinct(results);
    }

    private static void AddDirectoryCandidates(string directory, List<string> results)
    {
        string trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0)
        {
            return;
        }

        results.Add(Path.Combine(trimmed, "VALORANT.exe"));
        results.Add(Path.Combine(trimmed, "live", "VALORANT.exe"));

        string? parent = Path.GetDirectoryName(trimmed);
        if (!string.IsNullOrEmpty(parent))
        {
            results.Add(Path.Combine(parent, "VALORANT.exe"));
            results.Add(Path.Combine(parent, "live", "VALORANT.exe"));
        }
    }

    private static void AddLiveDirectoryCandidatesFromBinariesPath(string directory, List<string> results)
    {
        string trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? current = trimmed;
        while (!string.IsNullOrEmpty(current))
        {
            if (string.Equals(Path.GetFileName(current), "ShooterGame", StringComparison.OrdinalIgnoreCase))
            {
                string? liveDirectory = Path.GetDirectoryName(current);
                if (!string.IsNullOrEmpty(liveDirectory))
                {
                    results.Add(Path.Combine(liveDirectory, "VALORANT.exe"));
                }

                return;
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static void AddShippingCandidatesForDirectory(string directory, List<string> results)
    {
        results.Add(Path.Combine(
            directory, "ShooterGame", "Binaries", "Win64", "VALORANT-Win64-Shipping.exe"));
        results.Add(Path.Combine(
            directory, "live", "ShooterGame", "Binaries", "Win64", "VALORANT-Win64-Shipping.exe"));
    }

    private static CandidateResult? BuildCandidate(string? rawPath, bool acceptMissingFile)
    {
        string? cleaned = CleanEvidencePath(rawPath);
        if (cleaned is null || !Path.IsPathRooted(cleaned))
        {
            return null;
        }

        IReadOnlyList<string> executableCandidates = BuildExecutableCandidates(cleaned);
        IReadOnlyList<string> shippingCandidates = BuildShippingCandidates(cleaned);

        string? executable = executableCandidates.FirstOrDefault(SafeFileExists);
        string? shipping = shippingCandidates.FirstOrDefault(SafeFileExists);

        if (executable is null && shipping is null && !acceptMissingFile)
        {
            return null;
        }

        if (executable is null && shipping is not null)
        {
            executable = DeriveLauncherFromShipping(shipping);
        }

        executable ??= executableCandidates.FirstOrDefault();
        shipping ??= shippingCandidates.FirstOrDefault();

        if (executable is null && shipping is null)
        {
            return null;
        }

        string? installDirectory = executable is not null
            ? Path.GetDirectoryName(executable)
            : SafeDirectoryExists(cleaned) ? cleaned : null;

        bool verified = SafeFileExists(executable) || SafeFileExists(shipping);
        return new CandidateResult(executable, shipping, installDirectory, verified);
    }

    private static string? DeriveLauncherFromShipping(string shippingExecutablePath)
    {
        string? liveDirectory = shippingExecutablePath;
        for (int i = 0; i < 4 && !string.IsNullOrEmpty(liveDirectory); i++)
        {
            liveDirectory = Path.GetDirectoryName(liveDirectory);
        }

        return string.IsNullOrEmpty(liveDirectory)
            ? null
            : Path.Combine(liveDirectory, "VALORANT.exe");
    }

    // ---------- Path helpers ----------

    /// <summary>
    /// Cleans an evidence path: trims quotes/whitespace, strips an icon-index suffix such as ",0",
    /// normalizes slashes and returns a full path for rooted input.
    /// </summary>
    internal static string? CleanEvidencePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        string value = rawPath.Trim();
        if (value.StartsWith('"'))
        {
            value = value[1..];
        }

        int closingQuote = value.IndexOf('"');
        if (closingQuote >= 0)
        {
            value = value[..closingQuote];
        }

        value = value.Trim();

        int comma = value.LastIndexOf(',');
        if (comma > 0)
        {
            string suffix = value[(comma + 1)..].Trim();
            if (suffix.Length > 0 && suffix.All(char.IsAsciiDigit) && value[..comma].TrimEnd().Length > 0)
            {
                value = value[..comma].TrimEnd();
            }
        }

        value = value.Replace('/', Path.DirectorySeparatorChar).Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (!Path.IsPathRooted(value))
        {
            return value;
        }

        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value;
        }
    }

    private static string? TryReadFileVersion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !SafeFileExists(path))
        {
            return null;
        }

        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            string? version = NullIfBlank(info.FileVersion) ?? NullIfBlank(info.ProductVersion);
            return version;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static ValorantInstallation BuildInstallation(
        CandidateResult candidate,
        string detectionSource,
        List<string> notes)
    {
        notes.Add($"Detected VALORANT via {detectionSource}.");
        if (!candidate.VerifiedOnDisk)
        {
            notes.Add("The reported VALORANT path does not exist on disk; the install may be stale or relocated.");
        }

        string? version = TryReadFileVersion(candidate.ExecutablePath) ??
                          TryReadFileVersion(candidate.ShippingExecutablePath);

        return new ValorantInstallation
        {
            ExecutablePath = candidate.ExecutablePath,
            ShippingExecutablePath = candidate.ShippingExecutablePath,
            InstallDirectory = candidate.InstallDirectory,
            Version = version,
            DetectionSource = detectionSource,
            Notes = [.. notes],
        };
    }

    private static void AddRiotClientEvidence(string programDataPath, List<string> notes)
    {
        try
        {
            string riotClientDirectory = Path.Combine(programDataPath, "Riot Games", "Riot Client");
            if (Directory.Exists(riotClientDirectory))
            {
                notes.Add(
                    "Riot Client configuration is present; this is supporting evidence only and is not used as the game path.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Evidence-only probe; ignore failures.
        }
    }

    internal static IReadOnlyList<string> GetDefaultInstallRoots()
    {
        var roots = new List<string>();
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives()
                         .Where(drive => drive.DriveType == DriveType.Fixed)
                         .Take(8))
            {
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "Riot Games", "VALORANT"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Drive enumeration is optional; fall through to the C: default.
        }

        if (roots.Count == 0)
        {
            roots.Add(Path.Combine(@"C:\", "Riot Games", "VALORANT"));
        }

        return roots;
    }

    private static string GetDefaultProgramData()
    {
        string path = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return string.IsNullOrWhiteSpace(path) ? @"C:\ProgramData" : path;
    }

    private static bool SafeFileExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static bool SafeDirectoryExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> Distinct(List<string> paths) =>
        paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ProductSettingsEvidence(string? FullPath, string? PartialPath);

    private sealed record CandidateResult(
        string? ExecutablePath,
        string? ShippingExecutablePath,
        string? InstallDirectory,
        bool VerifiedOnDisk);
}
