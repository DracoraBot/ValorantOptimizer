using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Diagnostics;
using ValorantOptimizer.Optimizations;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests.Review;

/// <summary>
/// Tests for the seven read-only review cards. Every probe is a deterministic fake, every registry
/// wraps <see cref="FakeRegistryStore"/> with clean child-key names, and any file-system path used
/// by a test lives in a <see cref="TempDirectory"/>; the tests never read this PC's hardware or
/// write real settings.
/// </summary>
public sealed class ReviewCardsTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;
    private const string DefaultConfigRoot = @"C:\Users\Test\AppData\Local\VALORANT\Saved\Config";
    private const string DefaultTempPath = @"C:\Users\Test\AppData\Local\Temp";

    private static readonly string[] ExpectedIds =
    [
        "mouse-pointer-speed",
        "nvidia-settings",
        "memory-overview",
        "network-overview",
        "usb-device-power",
        "storage-overview",
        "valorant-config",
    ];

    // ---------------------------------------------------------------- descriptors

    [Fact]
    public void MousePointerSpeedReview_HasReadOnlyDescriptor()
    {
        AssertReadOnlyDescriptor(
            new MousePointerSpeedReview(),
            "mouse-pointer-speed",
            OptimizationCategory.Mouse,
            RiskLevel.Safe);
    }

    [Fact]
    public void NvidiaSettingsReview_HasReadOnlyExperimentalDescriptor()
    {
        AssertReadOnlyDescriptor(
            new NvidiaSettingsReview(),
            "nvidia-settings",
            OptimizationCategory.Gpu,
            RiskLevel.Experimental);
    }

    [Fact]
    public void MemoryOverviewReview_HasReadOnlyDescriptor()
    {
        AssertReadOnlyDescriptor(
            new MemoryOverviewReview(DefaultMemoryProbe()),
            "memory-overview",
            OptimizationCategory.Memory,
            RiskLevel.Safe);
    }

    [Fact]
    public void NetworkOverviewReview_HasReadOnlyDescriptor()
    {
        AssertReadOnlyDescriptor(
            new NetworkOverviewReview(),
            "network-overview",
            OptimizationCategory.Network,
            RiskLevel.Safe);
    }

    [Fact]
    public void UsbDevicePowerReview_HasReadOnlyDescriptor()
    {
        AssertReadOnlyDescriptor(
            new UsbDevicePowerReview(),
            "usb-device-power",
            OptimizationCategory.Usb,
            RiskLevel.Safe);
    }

    [Fact]
    public void StorageOverviewReview_HasReadOnlyDescriptor()
    {
        AssertReadOnlyDescriptor(
            new StorageOverviewReview(DefaultStorageProbe(), @"C:\", DefaultTempPath),
            "storage-overview",
            OptimizationCategory.Storage,
            RiskLevel.Safe);
    }

    [Fact]
    public void ValorantConfigReview_HasReadOnlyDescriptor()
    {
        AssertReadOnlyDescriptor(
            new ValorantConfigReview(new FakeValorantLocator(new ValorantInstallation()), DefaultConfigRoot),
            "valorant-config",
            OptimizationCategory.Valorant,
            RiskLevel.Safe);
    }

    // ------------------------------------------------------ mouse pointer speed

    [Fact]
    public async Task MousePointerSpeedReview_Assess_ReportsStoredSpeedAndWindowsDefault()
    {
        var registry = new ReviewRegistryStore();
        registry.SetString(
            RegistryHive.CurrentUser,
            MousePointerSpeedReview.RegistrySubKey,
            MousePointerSpeedReview.ValueName,
            "7");
        int writesBefore = registry.WriteCount;
        int deletesBefore = registry.DeleteCount;
        var review = new MousePointerSpeedReview();

        OptimizationAssessment assessment = await review.AssessAsync(CreateContext(registry));

        Assert.Equal(OptimizationState.ReviewRequired, assessment.State);
        Assert.Contains("7", assessment.Headline);
        Assert.False(string.IsNullOrWhiteSpace(assessment.BlockedReason));
        Assert.Contains("preference", assessment.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(assessment.Evidence, evidence => evidence.Label == "Pointer speed" && evidence.Value.Contains('7'));
        Assert.Contains(assessment.Evidence, evidence => evidence.Label == "Windows default" && evidence.Value == "10");
        Assert.Equal(writesBefore, registry.WriteCount);
        Assert.Equal(deletesBefore, registry.DeleteCount);
    }

    [Fact]
    public async Task MousePointerSpeedReview_Assess_MissingValue_ReportsWindowsDefault()
    {
        var registry = new ReviewRegistryStore();
        var review = new MousePointerSpeedReview();

        OptimizationAssessment assessment = await review.AssessAsync(CreateContext(registry));

        Assert.Equal(OptimizationState.ReviewRequired, assessment.State);
        Assert.Contains("default", assessment.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "Pointer speed" && evidence.Value.Contains("default", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, registry.WriteCount);
        Assert.Equal(0, registry.DeleteCount);
    }

    [Fact]
    public async Task MousePointerSpeedReview_Assess_UnexpectedValueKind_DoesNotGuess()
    {
        var registry = new ReviewRegistryStore();
        registry.SetDword(
            RegistryHive.CurrentUser,
            MousePointerSpeedReview.RegistrySubKey,
            MousePointerSpeedReview.ValueName,
            10);
        var review = new MousePointerSpeedReview();

        OptimizationAssessment assessment = await review.AssessAsync(CreateContext(registry));

        Assert.Equal(OptimizationState.ReviewRequired, assessment.State);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "Pointer speed" && evidence.Value.Contains("DWord", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MousePointerSpeedReview_Capture_IsMetadataOnly()
    {
        var registry = new ReviewRegistryStore();
        registry.SetString(
            RegistryHive.CurrentUser,
            MousePointerSpeedReview.RegistrySubKey,
            MousePointerSpeedReview.ValueName,
            "7");
        int writesBefore = registry.WriteCount;
        var review = new MousePointerSpeedReview();

        ChangeSet capture = await review.CaptureAsync(CreateContext(registry));

        Assert.Equal("mouse-pointer-speed", capture.OptimizationId);
        Assert.Empty(capture.Entries);
        Assert.Equal("true", capture.Metadata["reviewOnly"]);
        Assert.Equal("7", capture.Metadata["pointerSpeed"]);
        Assert.Equal("String", capture.Metadata["pointerSpeedKind"]);
        Assert.Equal("10", capture.Metadata["windowsDefault"]);
        Assert.Equal(writesBefore, registry.WriteCount);
    }

    // ------------------------------------------------------------ NVIDIA settings

    [Fact]
    public async Task NvidiaSettingsReview_Assess_NvidiaAdapter_ReturnsReviewWithDriverEvidence()
    {
        var registry = new ReviewRegistryStore();
        string nvidiaKey = NvidiaSettingsReview.DisplayClassKey + @"\0000";
        registry.SetString(RegistryHive.LocalMachine, nvidiaKey, NvidiaSettingsReview.DriverDescriptionValueName, "NVIDIA GeForce RTX 4090");
        registry.SetString(RegistryHive.LocalMachine, nvidiaKey, NvidiaSettingsReview.ProviderNameValueName, "NVIDIA");
        registry.SetString(RegistryHive.LocalMachine, nvidiaKey, NvidiaSettingsReview.DriverVersionValueName, "31.0.15.3699");

        string intelKey = NvidiaSettingsReview.DisplayClassKey + @"\0001";
        registry.SetString(RegistryHive.LocalMachine, intelKey, NvidiaSettingsReview.DriverDescriptionValueName, "Intel(R) UHD Graphics");
        registry.SetString(RegistryHive.LocalMachine, intelKey, NvidiaSettingsReview.ProviderNameValueName, "Intel Corporation");

        int writesBefore = registry.WriteCount;
        var review = new NvidiaSettingsReview();

        OptimizationAssessment assessment = await review.AssessAsync(CreateContext(registry));

        Assert.Equal(OptimizationState.ReviewRequired, assessment.State);
        Assert.Contains("NVIDIA", assessment.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NVIDIA Control Panel", assessment.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "NVIDIA GPU 1"
                && evidence.Value.Contains("RTX 4090", StringComparison.Ordinal)
                && evidence.Value.Contains("31.0.15.3699", StringComparison.Ordinal));
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "Display driver entries" && evidence.Value.Contains("2 inspected"));
        Assert.Equal(writesBefore, registry.WriteCount);
    }

    [Fact]
    public async Task NvidiaSettingsReview_Assess_NonNvidiaAdapter_ReturnsUnsupported()
    {
        var registry = new ReviewRegistryStore();
        string intelKey = NvidiaSettingsReview.DisplayClassKey + @"\0000";
        registry.SetString(RegistryHive.LocalMachine, intelKey, NvidiaSettingsReview.DriverDescriptionValueName, "Intel(R) UHD Graphics");
        registry.SetString(RegistryHive.LocalMachine, intelKey, NvidiaSettingsReview.ProviderNameValueName, "Intel Corporation");
        var review = new NvidiaSettingsReview();

        OptimizationAssessment assessment = await review.AssessAsync(CreateContext(registry));

        Assert.Equal(OptimizationState.Unsupported, assessment.State);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "NVIDIA GPU" && evidence.Value == "Not detected");
        Assert.False(string.IsNullOrWhiteSpace(assessment.BlockedReason));
    }

    [Fact]
    public async Task NvidiaSettingsReview_Capture_IsMetadataOnly()
    {
        var registry = new ReviewRegistryStore();
        string nvidiaKey = NvidiaSettingsReview.DisplayClassKey + @"\0000";
        registry.SetString(RegistryHive.LocalMachine, nvidiaKey, NvidiaSettingsReview.DriverDescriptionValueName, "NVIDIA GeForce RTX 4070");
        registry.SetString(RegistryHive.LocalMachine, nvidiaKey, NvidiaSettingsReview.ProviderNameValueName, "NVIDIA");
        registry.SetString(RegistryHive.LocalMachine, nvidiaKey, NvidiaSettingsReview.DriverVersionValueName, "32.0.15.5599");
        var review = new NvidiaSettingsReview();

        ChangeSet capture = await review.CaptureAsync(CreateContext(registry));

        Assert.Empty(capture.Entries);
        Assert.Equal("true", capture.Metadata["nvidiaDetected"]);
        Assert.Equal("1", capture.Metadata["adapterCount"]);
        Assert.Contains("RTX 4070", capture.Metadata["adapters"], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- memory review

    [Fact]
    public async Task MemoryOverviewReview_Assess_ReportsTotalUsedAndTopProcesses()
    {
        var probe = new FakeMemoryOverviewProbe
        {
            Snapshot = MemorySnapshot(
                new MemoryConsumer("Chrome", 3L * Gigabyte),
                new MemoryConsumer("Discord", 1L * Gigabyte)),
        };
        var registry = new ReviewRegistryStore();
        var review = new MemoryOverviewReview(probe);

        OptimizationAssessment assessment = await review.AssessAsync(CreateContext(registry));

        Assert.Equal(OptimizationState.ReviewRequired, assessment.State);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "RAM total" && evidence.Value == "32.0 GB");
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "RAM used" && evidence.Value == "8.0 GB (25% of total)");
        Assert.Contains(assessment.Evidence, evidence => evidence.Label == "Background process: Chrome");
        Assert.Contains(assessment.Evidence, evidence => evidence.Label == "Background process: Discord");
        Assert.Contains("RAM", assessment.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public async Task MemoryOverviewReview_Assess_ShowsAtMostThreeLargestProcesses()
    {
        var probe = new FakeMemoryOverviewProbe
        {
            Snapshot = MemorySnapshot(
                new MemoryConsumer("Bravo", 4L * Gigabyte),
                new MemoryConsumer("Alpha", 1L * Gigabyte),
                new MemoryConsumer("Charlie", 3L * Gigabyte),
                new MemoryConsumer("Delta", 2L * Gigabyte)),
        };
        var review = new MemoryOverviewReview(probe);

        OptimizationAssessment assessment = await review.AssessAsync(CreateContext(new ReviewRegistryStore()));

        List<AssessmentEvidence> processEvidence = assessment.Evidence
            .Where(evidence => evidence.Label.StartsWith("Background process:", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(3, processEvidence.Count);
        Assert.Contains(processEvidence, evidence => evidence.Label == "Background process: Bravo");
        Assert.Contains(processEvidence, evidence => evidence.Label == "Background process: Charlie");
        Assert.Contains(processEvidence, evidence => evidence.Label == "Background process: Delta");
        Assert.DoesNotContain(processEvidence, evidence => evidence.Label == "Background process: Alpha");
    }

    [Fact]
    public async Task MemoryOverviewReview_Assess_UnavailableProbe_ReturnsReviewWithoutGuessing()
    {
        var probe = new FakeMemoryOverviewProbe { Snapshot = null };
        var registry = new ReviewRegistryStore();
        var review = new MemoryOverviewReview(probe);

        OptimizationAssessment assessment = await review.AssessAsync(CreateContext(registry));

        Assert.Equal(OptimizationState.ReviewRequired, assessment.State);
        Assert.Contains("Unavailable", assessment.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(assessment.Evidence, evidence => evidence.Label == "RAM total" && evidence.Value == "Unavailable");
        Assert.Equal(0, registry.WriteCount);
        Assert.Equal(0, registry.DeleteCount);
    }

    [Fact]
    public async Task MemoryOverviewReview_Capture_IsMetadataOnly()
    {
        var probe = new FakeMemoryOverviewProbe
        {
            Snapshot = MemorySnapshot(new MemoryConsumer("Chrome", 2L * Gigabyte)),
        };
        var registry = new ReviewRegistryStore();
        var review = new MemoryOverviewReview(probe);

        ChangeSet capture = await review.CaptureAsync(CreateContext(registry));

        Assert.Empty(capture.Entries);
        Assert.Equal("true", capture.Metadata["reviewOnly"]);
        Assert.Equal("34359738368", capture.Metadata["totalPhysicalBytes"]);
        Assert.Equal("25769803776", capture.Metadata["availablePhysicalBytes"]);
        Assert.Equal("8589934592", capture.Metadata["usedPhysicalBytes"]);
        Assert.Contains("Chrome=", capture.Metadata["largestBackgroundProcesses"], StringComparison.Ordinal);
        Assert.Equal(0, registry.WriteCount);
    }

    // ------------------------------------------------------------ network review

    [Fact]
    public async Task NetworkOverviewReview_Assess_ReportsInterfacesAndDns()
    {
        var registry = new ReviewRegistryStore();
        const string staticInterface = @"{11111111-AAAA-4AAA-8AAA-111111111111}";
        const string dhcpInterface = @"{22222222-BBBB-4BBB-8BBB-222222222222}";
        registry.SetString(
            RegistryHive.LocalMachine,
            NetworkOverviewReview.InterfacesKey + "\\" + staticInterface,
            NetworkOverviewReview.NameServerValueName,
            "1.1.1.1,1.0.0.1");
        registry.SetString(
            RegistryHive.LocalMachine,
            NetworkOverviewReview.InterfacesKey + "\\" + dhcpInterface,
            NetworkOverviewReview.DhcpNameServerValueName,
            "192.168.1.1");
        int writesBefore = registry.WriteCount;
        var review = new NetworkOverviewReview();

        OptimizationAssessment assessment = await review.AssessAsync(CreateContext(registry));

        Assert.Equal(OptimizationState.ReviewRequired, assessment.State);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "Interfaces" && evidence.Value.StartsWith("2 stored", StringComparison.Ordinal));
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "Interfaces with DNS" && evidence.Value == "2 of 2");
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "DNS " + staticInterface
                && evidence.Value.Contains("1.1.1.1", StringComparison.Ordinal)
                && evidence.Value.Contains("static", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "DNS " + dhcpInterface
                && evidence.Value.Contains("192.168.1.1", StringComparison.Ordinal)
                && evidence.Value.Contains("DHCP", StringComparison.Ordinal));
        Assert.Equal(writesBefore, registry.WriteCount);
    }

    [Fact]
    public async Task NetworkOverviewReview_Assess_NoInterfaces_ReturnsReviewWithZeroCounts()
    {
        var registry = new ReviewRegistryStore();
        var review = new NetworkOverviewReview();

        OptimizationAssessment assessment = await review.AssessAsync(CreateContext(registry));

        Assert.Equal(OptimizationState.ReviewRequired, assessment.State);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "Interfaces" && evidence.Value.StartsWith("0 stored", StringComparison.Ordinal));
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "DNS"
                && evidence.Value.Contains("DHCP", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public async Task NetworkOverviewReview_Capture_IsMetadataOnly()
    {
        var registry = new ReviewRegistryStore();
        const string staticInterface = @"{33333333-CCCC-4CCC-8CCC-333333333333}";
        registry.SetString(
            RegistryHive.LocalMachine,
            NetworkOverviewReview.InterfacesKey + "\\" + staticInterface,
            NetworkOverviewReview.NameServerValueName,
            "9.9.9.9");
        var review = new NetworkOverviewReview();

        ChangeSet capture = await review.CaptureAsync(CreateContext(registry));

        Assert.Empty(capture.Entries);
        Assert.Equal("1", capture.Metadata["interfaceCount"]);
        Assert.Equal("1", capture.Metadata["dnsInterfaceCount"]);
        Assert.Contains("9.9.9.9", capture.Metadata["dnsServers"], StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- USB review

    [Fact]
    public async Task UsbDevicePowerReview_Assess_CountsDevicesStorageAndControllers()
    {
        var registry = new ReviewRegistryStore();
        registry.CreateKey(RegistryHive.LocalMachine, UsbDevicePowerReview.EnumUsbKey + @"\VID_1234&PID_0001");
        registry.CreateKey(RegistryHive.LocalMachine, UsbDevicePowerReview.EnumUsbKey + @"\VID_1234&PID_0002");
        registry.CreateKey(RegistryHive.LocalMachine, UsbDevicePowerReview.EnumUsbKey + @"\VID_5678&PID_0003");
        registry.CreateKey(RegistryHive.LocalMachine, UsbDevicePowerReview.EnumUsbStorageKey + @"\DISK&VEN_USB");
        registry.CreateKey(RegistryHive.LocalMachine, UsbDevicePowerReview.EnumUsbStorageKey + @"\DISK&VEN_FLASH");
        registry.CreateKey(RegistryHive.LocalMachine, UsbDevicePowerReview.UsbControllerClassKey + @"\0000");
        var review = new UsbDevicePowerReview();

        OptimizationAssessment assessment = await review.AssessAsync(CreateContext(registry));

        Assert.Equal(OptimizationState.ReviewRequired, assessment.State);
        Assert.Contains(assessment.Evidence, evidence => evidence.Label == "USB devices" && evidence.Value == "3");
        Assert.Contains(assessment.Evidence, evidence => evidence.Label == "USB storage devices" && evidence.Value == "2");
        Assert.Contains(assessment.Evidence, evidence => evidence.Label == "USB host controllers" && evidence.Value == "1");
        Assert.Contains("selective suspend", assessment.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public async Task UsbDevicePowerReview_Capture_IsMetadataOnly()
    {
        var registry = new ReviewRegistryStore();
        registry.CreateKey(RegistryHive.LocalMachine, UsbDevicePowerReview.EnumUsbKey + @"\VID_AAAA&PID_BBBB");
        var review = new UsbDevicePowerReview();

        ChangeSet capture = await review.CaptureAsync(CreateContext(registry));

        Assert.Empty(capture.Entries);
        Assert.Equal("1", capture.Metadata["usbDeviceCount"]);
        Assert.Equal("0", capture.Metadata["usbStorageDeviceCount"]);
        Assert.Equal("0", capture.Metadata["usbHostControllerCount"]);
    }

    // ------------------------------------------------------------ storage review

    [Fact]
    public async Task StorageOverviewReview_Assess_ReportsFreeSpaceAndTempSize()
    {
        var probe = new FakeStorageOverviewProbe
        {
            Drive = new SystemDriveSpace(@"C:\", 512L * Gigabyte, 128L * Gigabyte),
            TempBytes = 2L * Gigabyte,
        };
        var registry = new ReviewRegistryStore();
        var review = new StorageOverviewReview(probe, @"C:\", DefaultTempPath);

        OptimizationAssessment assessment = await review.AssessAsync(CreateContext(registry));

        Assert.Equal(OptimizationState.ReviewRequired, assessment.State);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "System drive"
                && evidence.Value == "128.0 GB free of 512.0 GB (25% free)");
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "Temp folder"
                && evidence.Value == "2.0 GB in " + DefaultTempPath);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "SMART health" && evidence.Value.Contains("Not probed"));
        Assert.Contains("SMART", assessment.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(@"C:\", probe.LastDriveRoot);
        Assert.Equal(DefaultTempPath, probe.LastDirectoryPath);
        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public async Task StorageOverviewReview_Assess_UnavailableDrive_ReturnsReviewWithoutGuessing()
    {
        var probe = new FakeStorageOverviewProbe { Drive = null, TempBytes = null };
        var review = new StorageOverviewReview(probe, @"Z:\", DefaultTempPath);

        OptimizationAssessment assessment = await review.AssessAsync(CreateContext(new ReviewRegistryStore()));

        Assert.Equal(OptimizationState.ReviewRequired, assessment.State);
        Assert.Contains("unavailable", assessment.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "System drive" && evidence.Value == @"Unavailable for Z:\");
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "Temp folder" && evidence.Value.StartsWith("Unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StorageOverviewReview_Capture_IsMetadataOnly()
    {
        var probe = new FakeStorageOverviewProbe
        {
            Drive = new SystemDriveSpace(@"C:\", 512L * Gigabyte, 128L * Gigabyte),
            TempBytes = 2L * Gigabyte,
        };
        var review = new StorageOverviewReview(probe, @"C:\", DefaultTempPath);

        ChangeSet capture = await review.CaptureAsync(CreateContext(new ReviewRegistryStore()));

        Assert.Empty(capture.Entries);
        Assert.Equal(@"C:\", capture.Metadata["systemDriveRoot"]);
        Assert.Equal("549755813888", capture.Metadata["systemDriveTotalBytes"]);
        Assert.Equal("137438953472", capture.Metadata["systemDriveFreeBytes"]);
        Assert.Equal("2147483648", capture.Metadata["tempDirectoryBytes"]);
    }

    [Fact]
    public void SystemStorageOverviewProbe_MeasuresTempDirectoryBounded()
    {
        using var temp = new TempDirectory("review-storage");
        File.WriteAllBytes(temp.Combine("a.bin"), new byte[1234]);
        Directory.CreateDirectory(temp.Combine("nested"));
        File.WriteAllBytes(temp.Combine("nested", "b.bin"), new byte[4321]);
        var probe = new SystemStorageOverviewProbe();

        long? measured = probe.MeasureDirectoryBytes(temp.Path);

        Assert.Equal(1234 + 4321, measured);
        Assert.Null(probe.MeasureDirectoryBytes(temp.Combine("does-not-exist")));
    }

    // ----------------------------------------------------------- VALORANT review

    [Fact]
    public async Task ValorantConfigReview_Assess_DetectedInstall_ReportsPathVersionAndConfig()
    {
        using var temp = new TempDirectory("review-valorant");
        string configRoot = temp.Combine("VALORANT", "Saved", "Config");
        Directory.CreateDirectory(configRoot);
        string installDirectory = temp.Combine("Riot Games", "VALORANT", "live");
        var locator = new FakeValorantLocator(new ValorantInstallation
        {
            ExecutablePath = Path.Combine(installDirectory, "VALORANT.exe"),
            InstallDirectory = installDirectory,
            Version = "release-10.09",
            DetectionSource = "uninstall registry",
        });
        var registry = new ReviewRegistryStore();
        var review = new ValorantConfigReview(locator, configRoot);

        OptimizationAssessment assessment = await review.AssessAsync(CreateContext(registry));

        Assert.Equal(OptimizationState.ReviewRequired, assessment.State);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "Install location" && evidence.Value == installDirectory);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "Version" && evidence.Value == "release-10.09");
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "User config folder"
                && evidence.Value.Contains("Present", StringComparison.Ordinal)
                && evidence.Value.Contains(configRoot, StringComparison.Ordinal));
        Assert.Contains("in-game", assessment.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, locator.Calls);
        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public async Task ValorantConfigReview_Assess_NotDetected_ReturnsReviewAndExplains()
    {
        using var temp = new TempDirectory("review-valorant-missing");
        var locator = new FakeValorantLocator(new ValorantInstallation());
        var review = new ValorantConfigReview(locator, temp.Combine("VALORANT", "Saved", "Config"));

        OptimizationAssessment assessment = await review.AssessAsync(CreateContext(new ReviewRegistryStore()));

        Assert.Equal(OptimizationState.ReviewRequired, assessment.State);
        Assert.NotEqual(OptimizationState.Unsupported, assessment.State);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "Install location" && evidence.Value == "Not detected");
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "User config folder"
                && evidence.Value.StartsWith("Not found", StringComparison.Ordinal));
        Assert.Contains("not detected", assessment.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValorantConfigReview_Assess_DetectedInstallWithoutConfigFolder_ReportsNotFound()
    {
        using var temp = new TempDirectory("review-valorant-noconfig");
        var locator = new FakeValorantLocator(new ValorantInstallation
        {
            ExecutablePath = Path.Combine(temp.Path, "VALORANT.exe"),
            InstallDirectory = temp.Path,
            Version = "release-10.09",
        });
        var review = new ValorantConfigReview(locator, temp.Combine("VALORANT", "Saved", "Config"));

        OptimizationAssessment assessment = await review.AssessAsync(CreateContext(new ReviewRegistryStore()));

        Assert.Equal(OptimizationState.ReviewRequired, assessment.State);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "User config folder"
                && evidence.Value.StartsWith("Not found", StringComparison.Ordinal));
        Assert.Contains("not found", assessment.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValorantConfigReview_Capture_IsMetadataOnly()
    {
        using var temp = new TempDirectory("review-valorant-capture");
        string configRoot = temp.Combine("VALORANT", "Saved", "Config");
        Directory.CreateDirectory(configRoot);
        var locator = new FakeValorantLocator(new ValorantInstallation
        {
            ExecutablePath = Path.Combine(temp.Path, "VALORANT.exe"),
            InstallDirectory = temp.Path,
            Version = "release-10.10",
            DetectionSource = "product settings",
        });
        var review = new ValorantConfigReview(locator, configRoot);

        ChangeSet capture = await review.CaptureAsync(CreateContext(new ReviewRegistryStore()));

        Assert.Empty(capture.Entries);
        Assert.Equal("true", capture.Metadata["reviewOnly"]);
        Assert.Equal(temp.Path, capture.Metadata["installDirectory"]);
        Assert.Equal("release-10.10", capture.Metadata["version"]);
        Assert.Equal("true", capture.Metadata["configRootExists"]);
    }

    [Fact]
    public void ValorantConfigReview_RequiresLocator()
    {
        Assert.Throws<ArgumentNullException>(() => new ValorantConfigReview(null!));
    }

    // ------------------------------------------------------------ shared behavior

    [Fact]
    public void AllReviewCards_HaveUniqueIdsAndPassCatalogValidation()
    {
        IReadOnlyList<IOptimization> cards = CreateCards();

        Assert.Equal(ExpectedIds, cards.Select(card => card.Descriptor.Id).ToArray());

        string[] duplicates = cards
            .GroupBy(card => card.Descriptor.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Assert.Empty(duplicates);
        Assert.Empty(OptimizationCatalogValidator.Validate(cards));
    }

    [Fact]
    public async Task AllReviewCards_ApplyAndRestore_AreRefusedWithoutWrites()
    {
        foreach (IOptimization card in CreateCards())
        {
            var registry = new ReviewRegistryStore();
            OptimizationContext context = CreateContext(registry);
            var original = new ChangeSet { OptimizationId = card.Descriptor.Id };

            OperationOutcome apply = await card.ApplyAsync(context, original);
            Assert.False(apply.Success);
            Assert.StartsWith("Review-only module", apply.Message, StringComparison.Ordinal);

            OperationOutcome restore = await card.RestoreAsync(context, original);
            Assert.False(restore.Success);
            Assert.StartsWith("Review-only module", restore.Message, StringComparison.Ordinal);

            Assert.Equal(0, registry.WriteCount);
            Assert.Equal(0, registry.DeleteCount);
            Assert.Empty(registry.Operations);
        }
    }

    [Fact]
    public async Task AllReviewCards_AssessAndCapture_NeverWriteToRegistry()
    {
        foreach (IOptimization card in CreateCards())
        {
            var registry = new ReviewRegistryStore();
            OptimizationContext context = CreateContext(registry);

            OptimizationAssessment assessment = await card.AssessAsync(context);
            Assert.True(
                assessment.State is OptimizationState.ReviewRequired or OptimizationState.Unsupported,
                $"{card.Descriptor.Id} returned {assessment.State}.");
            Assert.False(string.IsNullOrWhiteSpace(assessment.BlockedReason), $"{card.Descriptor.Id} has no blocked reason.");

            ChangeSet capture = await card.CaptureAsync(context);
            Assert.Equal(card.Descriptor.Id, capture.OptimizationId);
            Assert.Empty(capture.Entries);
            Assert.NotEmpty(capture.Metadata);
            Assert.Equal("true", capture.Metadata["reviewOnly"]);

            Assert.Equal(0, registry.WriteCount);
            Assert.Equal(0, registry.DeleteCount);
            Assert.Empty(registry.Operations);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static OptimizationContext CreateContext(ReviewRegistryStore registry) =>
        new(registry, new FakeProcessRunner(), isElevated: false);

    private static IReadOnlyList<IOptimization> CreateCards() =>
    [
        new MousePointerSpeedReview(),
        new NvidiaSettingsReview(),
        new MemoryOverviewReview(DefaultMemoryProbe()),
        new NetworkOverviewReview(),
        new UsbDevicePowerReview(),
        new StorageOverviewReview(DefaultStorageProbe(), @"C:\", DefaultTempPath),
        new ValorantConfigReview(new FakeValorantLocator(new ValorantInstallation()), DefaultConfigRoot),
    ];

    private static FakeMemoryOverviewProbe DefaultMemoryProbe() => new() { Snapshot = MemorySnapshot() };

    private static FakeStorageOverviewProbe DefaultStorageProbe() => new()
    {
        Drive = new SystemDriveSpace(@"C:\", 512L * Gigabyte, 128L * Gigabyte),
        TempBytes = 2L * Gigabyte,
    };

    private static MemoryOverviewSnapshot MemorySnapshot(params MemoryConsumer[] consumers) =>
        new(
            32UL * 1024 * 1024 * 1024,
            24UL * 1024 * 1024 * 1024,
            consumers);

    private static void AssertReadOnlyDescriptor(
        IOptimization card,
        string expectedId,
        OptimizationCategory expectedCategory,
        RiskLevel expectedRisk)
    {
        OptimizationDescriptor descriptor = card.Descriptor;

        Assert.Equal(expectedId, descriptor.Id);
        Assert.Equal(expectedCategory, descriptor.Category);
        Assert.Equal(expectedRisk, descriptor.Risk);
        Assert.False(descriptor.RequiresElevation);
        Assert.Equal(RestartRequirement.None, descriptor.Restart);
        Assert.Empty(descriptor.ManagedScopes);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Name));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Summary));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.WhatItChanges));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Why));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Compatibility));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.StateWhenApplied));
        Assert.NotEmpty(descriptor.Notes);
        Assert.DoesNotContain("fps", descriptor.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(OptimizationCatalogValidator.Validate([card]));
    }

    /// <summary>
    /// Decorator over <see cref="FakeRegistryStore"/> that normalizes child-key names. The shared
    /// fake appends its internal "|" separator to names discovered through its key index, while the
    /// production <c>RegistryStore</c> returns clean names; the review cards rely on the clean-name
    /// contract. Everything else is delegated unchanged, including the write/delete counters.
    /// </summary>
    private sealed class ReviewRegistryStore : IRegistryStore
    {
        private readonly FakeRegistryStore _inner = new();

        public int WriteCount => _inner.WriteCount;

        public int DeleteCount => _inner.DeleteCount;

        public List<string> Operations => _inner.Operations;

        public RegistryValue? ReadValue(RegistryHive hive, string subKey, string valueName) =>
            _inner.ReadValue(hive, subKey, valueName);

        public bool ValueExists(RegistryHive hive, string subKey, string valueName) =>
            _inner.ValueExists(hive, subKey, valueName);

        public IReadOnlyList<string> GetValueNames(RegistryHive hive, string subKey) =>
            _inner.GetValueNames(hive, subKey);

        public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string subKey) =>
            _inner.GetSubKeyNames(hive, subKey)
                .Select(name => name.TrimEnd('|'))
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        public bool KeyExists(RegistryHive hive, string subKey) => _inner.KeyExists(hive, subKey);

        public void WriteValue(
            RegistryHive hive,
            string subKey,
            string valueName,
            RegistryValueKind kind,
            object data) => _inner.WriteValue(hive, subKey, valueName, kind, data);

        public void DeleteValue(RegistryHive hive, string subKey, string valueName) =>
            _inner.DeleteValue(hive, subKey, valueName);

        public void CreateKey(RegistryHive hive, string subKey) => _inner.CreateKey(hive, subKey);

        public void SetString(RegistryHive hive, string subKey, string valueName, string value) =>
            _inner.SetString(hive, subKey, valueName, value);

        public void SetDword(RegistryHive hive, string subKey, string valueName, uint value) =>
            _inner.SetDword(hive, subKey, valueName, value);
    }

    private sealed class FakeMemoryOverviewProbe : IMemoryOverviewProbe
    {
        public MemoryOverviewSnapshot? Snapshot { get; set; }

        public int Reads { get; private set; }

        public MemoryOverviewSnapshot? Read()
        {
            Reads++;
            return Snapshot;
        }
    }

    private sealed class FakeStorageOverviewProbe : IStorageOverviewProbe
    {
        public SystemDriveSpace? Drive { get; set; }

        public long? TempBytes { get; set; }

        public string? LastDriveRoot { get; private set; }

        public string? LastDirectoryPath { get; private set; }

        public SystemDriveSpace? ReadDrive(string rootPath)
        {
            LastDriveRoot = rootPath;
            return Drive;
        }

        public long? MeasureDirectoryBytes(string directoryPath)
        {
            LastDirectoryPath = directoryPath;
            return TempBytes;
        }
    }

    private sealed class FakeValorantLocator : IValorantLocator
    {
        private readonly ValorantInstallation _installation;

        public FakeValorantLocator(ValorantInstallation installation) =>
            _installation = installation ?? throw new ArgumentNullException(nameof(installation));

        public int Calls { get; private set; }

        public Task<ValorantInstallation> LocateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(_installation);
        }
    }
}
