using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Diagnostics;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Diagnostics.Tests;

public sealed class SystemScannerTests
{
    private const string DisplayClass = SystemScanner.DisplayClassKey;

    [Fact]
    public void Scanner_is_parameterless_constructible()
    {
        var scanner = new SystemScanner();

        Assert.NotNull(scanner);
    }

    [Fact]
    public void ComposeOsBuildNumber_appends_the_ubr_revision()
    {
        string build = SystemScanner.ComposeOsBuildNumber("26100", 2314, new Version(10, 0, 19045));

        Assert.Equal("26100.2314", build);
    }

    [Fact]
    public void ComposeOsBuildNumber_uses_the_runtime_fallback_when_current_build_is_missing()
    {
        string build = SystemScanner.ComposeOsBuildNumber(null, null, new Version(10, 0, 22631, 0));

        Assert.Equal("22631", build);
    }

    [Fact]
    public void ComposeOsBuildNumber_ignores_a_zero_ubr()
    {
        string build = SystemScanner.ComposeOsBuildNumber("26100", 0, new Version(10, 0, 19045));

        Assert.Equal("26100", build);
    }

    [Fact]
    public void ComposeOsVersion_prefers_the_registry_build()
    {
        string version = SystemScanner.ComposeOsVersion(new Version(10, 0, 19045, 0), "26100");

        Assert.Equal("10.0.26100", version);
    }

    [Fact]
    public void ComposeOsVersion_falls_back_to_the_runtime_build()
    {
        string version = SystemScanner.ComposeOsVersion(new Version(10, 0, 22631, 0), null);

        Assert.Equal("10.0.22631", version);
    }

    [Theory]
    [InlineData("Windows 10 Pro", 26100, "Windows 11 Pro")]
    [InlineData("Windows 10 Enterprise LTSC", 22631, "Windows 11 Enterprise LTSC")]
    [InlineData("Windows 10 Pro", 19045, "Windows 10 Pro")]
    [InlineData("Windows 11 Pro", 26100, "Windows 11 Pro")]
    public void NormalizeOsProductName_relabels_only_windows_11_era_builds(
        string productName,
        int build,
        string expected)
    {
        Assert.Equal(expected, SystemScanner.NormalizeOsProductName(productName, build));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void NormalizeOsProductName_falls_back_to_a_generic_name(string? productName)
    {
        Assert.Equal("Windows", SystemScanner.NormalizeOsProductName(productName, 19045));
    }

    [Fact]
    public void ReadGpu_reads_name_vendor_driver_and_qword_memory()
    {
        var registry = new FakeRegistryStore();
        const string adapterKey = DisplayClass + @"\0000";
        registry.SetString(RegistryHive.LocalMachine, adapterKey, "DriverDesc", "NVIDIA GeForce RTX 4070");
        registry.SetString(RegistryHive.LocalMachine, adapterKey, "ProviderName", "NVIDIA");
        registry.SetString(RegistryHive.LocalMachine, adapterKey, "DriverVersion", "31.0.15.3623");
        registry.SetString(RegistryHive.LocalMachine, adapterKey, "DriverDate", "6-12-2024");
        registry.SetQword(RegistryHive.LocalMachine, adapterKey, SystemScanner.MemorySizeValueName, 8589934592);

        GpuInfo? gpu = SystemScanner.ReadGpu(registry, adapterKey);

        Assert.NotNull(gpu);
        Assert.Equal("NVIDIA GeForce RTX 4070", gpu.Name);
        Assert.Equal("NVIDIA", gpu.Vendor);
        Assert.Equal("31.0.15.3623", gpu.DriverVersion);
        Assert.Equal("6-12-2024", gpu.DriverDate);
        Assert.Equal(8589934592UL, gpu.MemoryBytes);
        Assert.False(gpu.IsSoftwareAdapter);
        Assert.True(gpu.IsNvidia);
    }

    [Fact]
    public void ReadGpu_returns_null_when_the_driver_description_is_missing()
    {
        var registry = new FakeRegistryStore();
        const string adapterKey = DisplayClass + @"\0000";
        registry.SetString(RegistryHive.LocalMachine, adapterKey, "DriverVersion", "1.0");

        Assert.Null(SystemScanner.ReadGpu(registry, adapterKey));
    }

    [Fact]
    public void ReadGpu_flags_microsoft_basic_render_driver_as_a_software_adapter()
    {
        var registry = new FakeRegistryStore();
        const string adapterKey = DisplayClass + @"\0000";
        registry.SetString(RegistryHive.LocalMachine, adapterKey, "DriverDesc", "Microsoft Basic Render Driver");

        GpuInfo? gpu = SystemScanner.ReadGpu(registry, adapterKey);

        Assert.NotNull(gpu);
        Assert.True(gpu.IsSoftwareAdapter);
    }

    [Fact]
    public void ReadGpu_flags_virtual_display_adapters_as_software()
    {
        var registry = new FakeRegistryStore();
        const string adapterKey = DisplayClass + @"\0000";
        registry.SetString(RegistryHive.LocalMachine, adapterKey, "DriverDesc", "Parsec Virtual Display Adapter");

        GpuInfo? gpu = SystemScanner.ReadGpu(registry, adapterKey);

        Assert.NotNull(gpu);
        Assert.True(gpu.IsSoftwareAdapter);
    }

    [Fact]
    public void TryParseMemoryBytes_accepts_a_qword_and_rejects_other_kinds()
    {
        var qword = new RegistryValue(
            RegistryHive.LocalMachine, "adapter", SystemScanner.MemorySizeValueName,
            RegistryValueKind.QWord, unchecked((long)4294967296UL));
        var dword = new RegistryValue(
            RegistryHive.LocalMachine, "adapter", SystemScanner.MemorySizeValueName,
            RegistryValueKind.String, "4294967296");
        var warnings = new List<string>();

        Assert.Equal(4294967296UL, SystemScanner.TryParseMemoryBytes(qword));
        Assert.Null(SystemScanner.TryParseMemoryBytes(dword, warnings));
        Assert.Null(SystemScanner.TryParseMemoryBytes(null));
        Assert.Contains(warnings, warning => warning.Contains("REG_QWORD", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadGpus_skips_empty_adapters_and_orders_numeric_subkeys()
    {
        var registry = new FakeRegistryStore();
        registry.SetString(RegistryHive.LocalMachine, DisplayClass + @"\0002", "DriverDesc", "GPU B");
        registry.SetString(RegistryHive.LocalMachine, DisplayClass + @"\0000", "DriverDesc", "GPU A");
        registry.CreateKey(RegistryHive.LocalMachine, DisplayClass + @"\0010");

        List<GpuInfo> gpus = SystemScanner.ReadGpus(registry);

        Assert.Equal(["GPU A", "GPU B"], gpus.Select(gpu => gpu.Name));
    }

    [Fact]
    public void CreateDisplayInfo_maps_mode_values_and_clamps_negatives()
    {
        DisplayInfo? display = SystemScanner.CreateDisplayInfo(@"\\.\DISPLAY1", 1920, 1080, -1, 32);

        Assert.NotNull(display);
        Assert.Equal(@"\\.\DISPLAY1", display.DeviceName);
        Assert.Equal(1920, display.Width);
        Assert.Equal(1080, display.Height);
        Assert.Equal(0, display.RefreshHz);
        Assert.Equal(32, display.BitsPerPixel);
    }

    [Fact]
    public void CreateDisplayInfo_returns_null_for_an_invalid_mode()
    {
        Assert.Null(SystemScanner.CreateDisplayInfo(null, 0, 1080, 60, 32));
    }

    [Fact]
    public void CountPhysicalCores_counts_only_processor_core_records()
    {
        byte[] buffer = RelationBuffer((0, 32), (0, 32), (1, 32));

        Assert.Equal(2, SystemScanner.CountPhysicalCores(buffer));
    }

    [Fact]
    public void CountPhysicalCores_returns_null_for_a_malformed_buffer()
    {
        Assert.Null(SystemScanner.CountPhysicalCores([]));
        Assert.Null(SystemScanner.CountPhysicalCores([1, 2, 3]));
        Assert.Null(SystemScanner.CountPhysicalCores(RelationBuffer((0, 32))[..16]));
    }

    [Fact]
    public void CountPhysicalCoreRecords_counts_core_records_in_the_legacy_layout()
    {
        byte[] buffer = LegacyBuffer(recordSize: 8, 1, 0, 1, 0);

        Assert.Equal(2, SystemScanner.CountPhysicalCoreRecords(buffer, recordSize: 8, relationshipOffset: 4));
        Assert.Null(SystemScanner.CountPhysicalCoreRecords(buffer, recordSize: 7, relationshipOffset: 4));
    }

    [Fact]
    public async Task ScanAsync_composes_a_snapshot_from_registry_data_and_probes()
    {
        var registry = new FakeRegistryStore();
        SeedOs(registry, "Windows 10 Pro", "24H2", "26100", 2314);
        SeedCpu(registry, "Test CPU @ 5.00GHz", 5000);
        const string adapterKey = DisplayClass + @"\0000";
        registry.SetString(RegistryHive.LocalMachine, adapterKey, "DriverDesc", "NVIDIA GeForce RTX 4070");
        registry.SetString(RegistryHive.LocalMachine, adapterKey, "ProviderName", "NVIDIA");
        registry.SetString(RegistryHive.LocalMachine, adapterKey, "DriverVersion", "31.0.15.3623");
        registry.SetString(RegistryHive.LocalMachine, adapterKey, "DriverDate", "6-12-2024");
        registry.SetQword(RegistryHive.LocalMachine, adapterKey, SystemScanner.MemorySizeValueName, 8589934592);

        var probe = new FakeSystemProbe
        {
            TotalMemoryBytes = 34359738368,
            PhysicalCoreCount = 12,
            PrimaryDisplay = new DisplayInfo(@"\\.\DISPLAY1", 2560, 1440, 240, 32),
            Elevated = true,
        };

        SystemSnapshot snapshot = await new SystemScanner(registry, probe).ScanAsync();

        Assert.Equal("Windows 11 Pro", snapshot.OsName);
        Assert.Equal("10.0.26100", snapshot.OsVersion);
        Assert.Equal("26100.2314", snapshot.OsBuild);
        Assert.Equal("24H2", snapshot.OsDisplayVersion);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Architecture));
        Assert.Equal("Test CPU @ 5.00GHz", snapshot.Cpu.Name);
        Assert.Equal(12, snapshot.Cpu.PhysicalCores);
        Assert.Equal(5000, snapshot.Cpu.MaxClockMhz);
        Assert.True(snapshot.Cpu.LogicalProcessors >= 1);
        Assert.Equal(34359738368UL, snapshot.TotalMemoryBytes);
        Assert.Equal(2560, snapshot.PrimaryDisplay?.Width);
        Assert.True(snapshot.IsElevated);
        Assert.Single(snapshot.Gpus);
        Assert.Equal("NVIDIA GeForce RTX 4070", snapshot.PrimaryGpu?.Name);
        Assert.Empty(snapshot.Warnings);
    }

    [Fact]
    public async Task ScanAsync_records_warnings_and_fallbacks_when_probes_are_unavailable()
    {
        var registry = new FakeRegistryStore();
        var probe = new FakeSystemProbe
        {
            TotalMemoryBytes = null,
            PhysicalCoreCount = null,
            PrimaryDisplay = null,
            Elevated = null,
        };

        SystemSnapshot snapshot = await new SystemScanner(registry, probe).ScanAsync();

        Assert.Equal("Windows", snapshot.OsName);
        Assert.Equal("Unknown CPU", snapshot.Cpu.Name);
        Assert.Equal(0UL, snapshot.TotalMemoryBytes);
        Assert.Null(snapshot.PrimaryDisplay);
        Assert.False(snapshot.IsElevated);
        Assert.Empty(snapshot.Gpus);
        Assert.Contains(snapshot.Warnings, warning => warning.Contains("Windows version registry", StringComparison.Ordinal));
        Assert.Contains(snapshot.Warnings, warning => warning.Contains("CPU name", StringComparison.Ordinal));
        Assert.Contains(snapshot.Warnings, warning => warning.Contains("Total physical memory", StringComparison.Ordinal));
        Assert.Contains(snapshot.Warnings, warning => warning.Contains("Primary display", StringComparison.Ordinal));
        Assert.Contains(snapshot.Warnings, warning => warning.Contains("Elevation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScanAsync_honors_a_pre_cancelled_token()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SystemScanner().ScanAsync(cancellation.Token));
    }

    private static void SeedOs(
        FakeRegistryStore registry,
        string productName,
        string displayVersion,
        string currentBuild,
        uint ubr)
    {
        registry.SetString(RegistryHive.LocalMachine, SystemScanner.WindowsVersionKey, "ProductName", productName);
        registry.SetString(RegistryHive.LocalMachine, SystemScanner.WindowsVersionKey, "DisplayVersion", displayVersion);
        registry.SetString(RegistryHive.LocalMachine, SystemScanner.WindowsVersionKey, "CurrentBuild", currentBuild);
        registry.SetDword(RegistryHive.LocalMachine, SystemScanner.WindowsVersionKey, "UBR", ubr);
    }

    private static void SeedCpu(FakeRegistryStore registry, string name, uint maxClockMhz)
    {
        registry.SetString(RegistryHive.LocalMachine, SystemScanner.ProcessorKey, "ProcessorNameString", name);
        registry.SetDword(RegistryHive.LocalMachine, SystemScanner.ProcessorKey, "~MHz", maxClockMhz);
    }

    /// <summary>Builds a GetLogicalProcessorInformationEx-shaped buffer.</summary>
    private static byte[] RelationBuffer(params (int Relationship, int Size)[] records)
    {
        var buffer = new List<byte>();
        foreach ((int relationship, int size) in records)
        {
            buffer.AddRange(BitConverter.GetBytes(relationship));
            buffer.AddRange(BitConverter.GetBytes(size));
            buffer.AddRange(new byte[size - 8]);
        }

        return [.. buffer];
    }

    /// <summary>Builds a legacy GetLogicalProcessorInformation-shaped buffer.</summary>
    private static byte[] LegacyBuffer(int recordSize, params int[] relationships)
    {
        var buffer = new List<byte>();
        foreach (int relationship in relationships)
        {
            var record = new byte[recordSize];
            BitConverter.GetBytes(relationship).CopyTo(record, 4);
            buffer.AddRange(record);
        }

        return [.. buffer];
    }
}
