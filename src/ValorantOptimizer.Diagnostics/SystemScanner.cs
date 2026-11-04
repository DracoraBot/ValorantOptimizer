using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Diagnostics;

/// <summary>
/// Read-only snapshot of the machine. Never requires administrator rights and never writes.
/// A failed probe is recorded in <see cref="SystemSnapshot.Warnings"/> and replaced with a safe
/// fallback instead of failing the whole scan.
/// </summary>
public sealed class SystemScanner : ISystemScanner
{
    internal const string WindowsVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    internal const string ProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    internal const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    internal const string MemorySizeValueName = "HardwareInformation.qwMemorySize";

    private readonly IRegistryStore _registry;
    private readonly ISystemProbe _probe;

    /// <summary>Production scanner: live registry plus real Win32 probes.</summary>
    public SystemScanner()
        : this(new RegistryStore(), new Win32SystemProbe())
    {
    }

    /// <summary>Test seam: inject a fake registry and/or fake hardware probe.</summary>
    internal SystemScanner(IRegistryStore registry, ISystemProbe probe)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public Task<SystemSnapshot> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Collect());
    }

    private SystemSnapshot Collect()
    {
        var warnings = new List<string>();
        Version environmentVersion = Environment.OSVersion.Version;

        string? productName = ReadStringValue(WindowsVersionKey, "ProductName", warnings);
        string? displayVersion = ReadStringValue(WindowsVersionKey, "DisplayVersion", warnings);
        string? currentBuild = ReadStringValue(WindowsVersionKey, "CurrentBuild", warnings);
        int? ubr = ToInt(ReadDwordValue(WindowsVersionKey, "UBR", warnings));

        if (string.IsNullOrWhiteSpace(productName) || string.IsNullOrWhiteSpace(currentBuild))
        {
            warnings.Add("Windows version registry values were incomplete; using Environment fallbacks.");
        }

        string osBuild = ComposeOsBuildNumber(currentBuild, ubr, environmentVersion);
        int buildNumber = ParseBuildNumber(osBuild);
        string osVersion = ComposeOsVersion(environmentVersion, currentBuild);
        string osName = NormalizeOsProductName(productName, buildNumber);

        CpuInfo cpu = ReadCpu(warnings);
        List<GpuInfo> gpus = ReadGpus(_registry, warnings);

        ulong totalMemory = 0;
        try
        {
            ulong? probed = _probe.GetTotalMemoryBytes();
            if (probed is > 0)
            {
                totalMemory = probed.Value;
            }
            else
            {
                warnings.Add("Total physical memory could not be read.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"Total physical memory probe failed: {ex.Message}");
        }

        DisplayInfo? display;
        try
        {
            display = _probe.GetPrimaryDisplay();
            if (display is null)
            {
                warnings.Add("Primary display settings could not be read.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            display = null;
            warnings.Add($"Primary display probe failed: {ex.Message}");
        }

        bool isElevated = false;
        try
        {
            bool? probed = _probe.GetIsElevated();
            if (probed is null)
            {
                warnings.Add("Elevation status could not be determined.");
            }
            else
            {
                isElevated = probed.Value;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"Elevation probe failed: {ex.Message}");
        }

        return new SystemSnapshot
        {
            OsName = osName,
            OsVersion = osVersion,
            OsBuild = osBuild,
            OsDisplayVersion = NullIfBlank(displayVersion),
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            Cpu = cpu,
            Gpus = gpus,
            TotalMemoryBytes = totalMemory,
            PrimaryDisplay = display,
            IsElevated = isElevated,
            ScannedAt = DateTimeOffset.Now,
            Warnings = warnings,
        };
    }

    private CpuInfo ReadCpu(ICollection<string> warnings)
    {
        string? name = ReadStringValue(ProcessorKey, "ProcessorNameString", warnings)?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Unknown CPU";
            warnings.Add("CPU name could not be read from the registry.");
        }

        int? maxClockMhz = ToInt(ReadDwordValue(ProcessorKey, "~MHz", warnings));

        int? physicalCores = null;
        try
        {
            physicalCores = _probe.GetPhysicalCoreCount();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"Physical core count probe failed: {ex.Message}");
        }

        if (physicalCores is null)
        {
            warnings.Add("Physical core count is unavailable; only the logical processor count is reported.");
        }

        return new CpuInfo(
            name,
            physicalCores,
            Math.Max(Environment.ProcessorCount, 1),
            maxClockMhz);
    }

    // ---------- OS helpers ----------

    /// <summary>
    /// Builds the dotted build string from <c>CurrentBuild</c> and the <c>UBR</c> revision, e.g.
    /// "26100" + UBR 2314 -> "26100.2314". Falls back to the .NET runtime build when the registry
    /// values are missing.
    /// </summary>
    internal static string ComposeOsBuildNumber(string? currentBuild, int? ubr, Version fallbackVersion)
    {
        string build;
        if (!string.IsNullOrWhiteSpace(currentBuild))
        {
            build = currentBuild.Trim();
        }
        else
        {
            build = fallbackVersion.Build > 0
                ? fallbackVersion.Build.ToString(CultureInfo.InvariantCulture)
                : "0";
        }

        return ubr is > 0
            ? build + "." + ubr.Value.ToString(CultureInfo.InvariantCulture)
            : build;
    }

    /// <summary>Builds "major.minor.build" using the registry build when available.</summary>
    internal static string ComposeOsVersion(Version fallbackVersion, string? currentBuild)
    {
        string prefix = string.Create(
            CultureInfo.InvariantCulture,
            $"{fallbackVersion.Major}.{fallbackVersion.Minor}.");

        if (!string.IsNullOrWhiteSpace(currentBuild) &&
            int.TryParse(currentBuild.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int build) &&
            build > 0)
        {
            return prefix + build.ToString(CultureInfo.InvariantCulture);
        }

        return fallbackVersion.Build > 0
            ? prefix + fallbackVersion.Build.ToString(CultureInfo.InvariantCulture)
            : $"{fallbackVersion.Major}.{fallbackVersion.Minor}";
    }

    /// <summary>
    /// Windows 11 still reports "Windows 10 ..." in <c>ProductName</c>; builds 22000 and later are
    /// relabelled for display. Unknown/empty names fall back to "Windows".
    /// </summary>
    internal static string NormalizeOsProductName(string? productName, int buildNumber)
    {
        string name = string.IsNullOrWhiteSpace(productName) ? "Windows" : productName.Trim();
        const string windows10 = "Windows 10";

        if (buildNumber >= 22000 &&
            name.StartsWith(windows10, StringComparison.OrdinalIgnoreCase))
        {
            name = "Windows 11" + name[windows10.Length..];
        }

        return name;
    }

    internal static int ParseBuildNumber(string? build)
    {
        if (string.IsNullOrWhiteSpace(build))
        {
            return 0;
        }

        int dot = build.IndexOf('.');
        string major = dot < 0 ? build : build[..dot];
        return int.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;
    }

    // ---------- GPU helpers ----------

    /// <summary>
    /// Reads every numeric adapter subkey under the display class key. Software adapters
    /// (Microsoft Basic Display/Render, Remote Desktop) are kept but flagged with
    /// <see cref="GpuInfo.IsSoftwareAdapter"/> so the UI can exclude them from recommendations;
    /// adapters without a <c>DriverDesc</c> are skipped entirely.
    /// </summary>
    internal static List<GpuInfo> ReadGpus(IRegistryStore registry, ICollection<string>? warnings = null)
    {
        var gpus = new List<GpuInfo>();

        IReadOnlyList<string> subKeys;
        try
        {
            subKeys = registry.GetSubKeyNames(RegistryHive.LocalMachine, DisplayClassKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings?.Add($"Display adapter registry enumeration failed: {ex.Message}");
            return gpus;
        }

        foreach (string subKey in subKeys
                     .Where(IsNumericSubKey)
                     .OrderBy(subKey => int.Parse(subKey, CultureInfo.InvariantCulture)))
        {
            GpuInfo? gpu = ReadGpu(registry, DisplayClassKey + "\\" + subKey, warnings);
            if (gpu is not null)
            {
                gpus.Add(gpu);
            }
        }

        if (gpus.Count == 0)
        {
            warnings?.Add("No display adapters were reported by the registry.");
        }

        return gpus;
    }

    /// <summary>Reads one adapter subkey into a <see cref="GpuInfo"/> (null for an empty name).</summary>
    internal static GpuInfo? ReadGpu(
        IRegistryStore registry,
        string adapterSubKey,
        ICollection<string>? warnings = null)
    {
        string? name = ReadStringValue(registry, adapterSubKey, "DriverDesc", warnings)?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string? vendor = NullIfBlank(ReadStringValue(registry, adapterSubKey, "ProviderName", warnings));
        string? driverVersion = NullIfBlank(ReadStringValue(registry, adapterSubKey, "DriverVersion", warnings));
        string? driverDate = NullIfBlank(ReadStringValue(registry, adapterSubKey, "DriverDate", warnings));
        ulong? memoryBytes = TryParseMemoryBytes(
            TryReadValue(registry, adapterSubKey, MemorySizeValueName, warnings),
            warnings);

        return new GpuInfo(name, vendor, driverVersion, driverDate, memoryBytes, IsSoftwareAdapter(name));
    }

    /// <summary>
    /// Parses <c>HardwareInformation.qwMemorySize</c>. The value is a REG_QWORD; a different kind
    /// is reported as a warning and ignored rather than guessed.
    /// </summary>
    internal static ulong? TryParseMemoryBytes(RegistryValue? value, ICollection<string>? warnings = null)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Kind != RegistryValueKind.QWord)
        {
            warnings?.Add(
                $"Display memory value '{value.ValueName}' was {value.Kind}; expected REG_QWORD.");
            return null;
        }

        ulong? bytes = value.Data switch
        {
            long signed => unchecked((ulong)signed),
            ulong unsigned => unsigned,
            int signedInt when signedInt > 0 => (ulong)signedInt,
            uint unsignedInt => unsignedInt,
            _ => null,
        };

        return bytes is > 0 ? bytes : null;
    }

    /// <summary>
    /// Detects the placeholder/virtual display adapters Windows reports without a physical GPU:
    /// Microsoft Basic Display/Render, remote/indirect displays and software virtual displays
    /// (for example Parsec's virtual display adapter). Flagged adapters stay in
    /// <see cref="SystemSnapshot.Gpus"/> but are skipped by
    /// <see cref="SystemSnapshot.PrimaryGpu"/>.
    /// </summary>
    internal static bool IsSoftwareAdapter(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Remote Desktop", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Indirect Display", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Hyper-V Video", StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Display helpers ----------

    /// <summary>Creates display info from raw mode fields; null for an invalid (headless) mode.</summary>
    internal static DisplayInfo? CreateDisplayInfo(
        string? deviceName,
        int width,
        int height,
        int refreshHz,
        int bitsPerPixel)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        return new DisplayInfo(
            NullIfBlank(deviceName),
            width,
            height,
            Math.Max(refreshHz, 0),
            Math.Max(bitsPerPixel, 0));
    }

    // ---------- CPU topology helpers ----------

    /// <summary>
    /// Counts <c>RelationProcessorCore</c> records in a GetLogicalProcessorInformationEx buffer.
    /// Each record is a DWORD relationship followed by a DWORD byte-length. Returns null when the
    /// buffer cannot be parsed or contains no physical core records.
    /// </summary>
    internal static int? CountPhysicalCores(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8)
        {
            return null;
        }

        int offset = 0;
        int records = 0;
        int cores = 0;
        while (offset < buffer.Length)
        {
            if (offset + 8 > buffer.Length)
            {
                return null;
            }

            int relationship = BitConverter.ToInt32(buffer[offset..(offset + 4)]);
            int size = BitConverter.ToInt32(buffer[(offset + 4)..(offset + 8)]);
            if (size < 8 || offset + size > buffer.Length)
            {
                return null;
            }

            records++;
            if (relationship == NativeMethods.RelationProcessorCore)
            {
                cores++;
            }

            offset += size;
        }

        return records > 0 && cores > 0 ? cores : null;
    }

    /// <summary>
    /// Counts processor-core records in the legacy GetLogicalProcessorInformation buffer, whose
    /// fixed stride depends on the architecture. Exposed for the fallback probe's unit test.
    /// </summary>
    internal static int? CountPhysicalCoreRecords(
        ReadOnlySpan<byte> buffer,
        int recordSize,
        int relationshipOffset)
    {
        if (recordSize < relationshipOffset + 4 ||
            relationshipOffset < 0 ||
            buffer.Length < recordSize ||
            buffer.Length % recordSize != 0)
        {
            return null;
        }

        int cores = 0;
        for (int offset = 0; offset < buffer.Length; offset += recordSize)
        {
            int relationship = BitConverter.ToInt32(
                buffer[(offset + relationshipOffset)..(offset + relationshipOffset + 4)]);
            if (relationship == NativeMethods.RelationProcessorCore)
            {
                cores++;
            }
        }

        return cores > 0 ? cores : null;
    }

    // ---------- Registry read helpers ----------

    private static RegistryValue? TryReadValue(
        IRegistryStore registry,
        string subKey,
        string valueName,
        ICollection<string>? warnings)
    {
        try
        {
            return registry.ReadValue(RegistryHive.LocalMachine, subKey, valueName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings?.Add($@"Could not read HKLM\{subKey}\{valueName}: {ex.Message}");
            return null;
        }
    }

    private static string? ReadStringValue(
        IRegistryStore registry,
        string subKey,
        string valueName,
        ICollection<string>? warnings)
    {
        RegistryValue? value = TryReadValue(registry, subKey, valueName, warnings);
        return value?.Data switch
        {
            string text => text,
            null => null,
            object other => Convert.ToString(other, CultureInfo.InvariantCulture),
        };
    }

    private string? ReadStringValue(string subKey, string valueName, ICollection<string> warnings) =>
        ReadStringValue(_registry, subKey, valueName, warnings);

    private uint? ReadDwordValue(string subKey, string valueName, ICollection<string> warnings)
    {
        RegistryValue? value = TryReadValue(_registry, subKey, valueName, warnings);
        if (value is null)
        {
            return null;
        }

        if (value.Kind != RegistryValueKind.DWord)
        {
            warnings.Add($@"Value HKLM\{subKey}\{valueName} was {value.Kind}; expected REG_DWORD.");
            return null;
        }

        return value.Data switch
        {
            int signed => unchecked((uint)signed),
            uint unsigned => unsigned,
            _ => null,
        };
    }

    private static bool IsNumericSubKey(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static int? ToInt(uint? value) =>
        value is null ? null : value.Value > int.MaxValue ? int.MaxValue : (int)value.Value;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
