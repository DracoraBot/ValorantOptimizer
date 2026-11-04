using System.Globalization;
using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests;

public sealed class NetworkAdapterTests
{
    private const string AdapterClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    private sealed class Fixture
    {
        public Fixture()
        {
            Context = new OptimizationContext(Registry, Processes, isElevated: false);
        }

        public FakeRegistryStore Registry { get; } = new();

        public FakeProcessRunner Processes { get; } = new();

        public OptimizationContext Context { get; }

        public NetworkAdapterPowerOptimization Optimization { get; } = new();

        public void Seed(
            string instanceName,
            string driverDesc,
            int? pnpCapabilities = 24,
            RegistryValueKind pnpKind = RegistryValueKind.DWord,
            int? characteristics = null,
            int? ifType = null) =>
            NetworkAdapterTestData.SeedAdapter(
                Registry,
                instanceName,
                driverDesc,
                pnpCapabilities,
                pnpKind,
                characteristics,
                ifType);

        public RegistryValue? ReadPnp(string instanceName) =>
            NetworkAdapterTestData.ReadPnpCapabilities(Registry, instanceName);

        public int ReadPnpValue(string instanceName) =>
            Convert.ToInt32(ReadPnp(instanceName)!.Data, CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Descriptor_HasExpectedIdentityAndScope()
    {
        OptimizationDescriptor descriptor = new NetworkAdapterPowerOptimization().Descriptor;

        Assert.Equal("network-adapter-power", descriptor.Id);
        Assert.Equal(OptimizationCategory.Network, descriptor.Category);
        Assert.Equal(RiskLevel.Advanced, descriptor.Risk);
        Assert.True(descriptor.RequiresElevation);
        Assert.Equal(RestartRequirement.None, descriptor.Restart);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Summary));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.WhatItChanges));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Why));
        Assert.NotEmpty(descriptor.Notes);

        RegistryScope scope = Assert.Single(descriptor.ManagedScopes);
        Assert.Equal(RegistryHive.LocalMachine, scope.Hive);
        Assert.Equal(AdapterClassKey, scope.SubKey);
    }

    [Fact]
    public async Task Assess_WhenNoPhysicalAdapters_IsUnsupported()
    {
        var fixture = new Fixture();

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Unsupported, assessment.State);
        Assert.False(string.IsNullOrWhiteSpace(assessment.BlockedReason));
    }

    [Fact]
    public async Task Assess_WhenAllTargetsHave24_IsApplied()
    {
        var fixture = new Fixture();
        fixture.Seed("0000", "Intel(R) Ethernet Controller I225-V", pnpCapabilities: 24);
        fixture.Seed("0001", "Intel(R) Wi-Fi 6 AX201 160MHz", pnpCapabilities: 24);

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
        Assert.Equal(2, assessment.Evidence.Count);
        Assert.All(assessment.Evidence, item =>
            Assert.Contains("cannot turn this adapter off", item.Value, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Assess_WhenOneTargetLacksTheValue_IsNotApplied()
    {
        var fixture = new Fixture();
        fixture.Seed("0000", "Intel(R) Ethernet Controller I225-V", pnpCapabilities: 24);
        fixture.Seed("0001", "Realtek PCIe GbE Family Controller", pnpCapabilities: null);

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains(
            assessment.Evidence,
            item => item.Value.Contains("not set", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Assess_WhenValueHasUnexpectedKind_IsNotApplied()
    {
        var fixture = new Fixture();
        fixture.Seed(
            "0000",
            "Intel(R) Ethernet Controller I225-V",
            pnpCapabilities: 24,
            pnpKind: RegistryValueKind.String);

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains(
            assessment.Evidence,
            item => item.Value.Contains("unexpected registry type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Assess_SkipsVirtualVpnMiniportAndLoopbackAdapters()
    {
        var fixture = new Fixture();
        fixture.Seed("0000", "Intel(R) Ethernet Controller I225-V", pnpCapabilities: 24);
        fixture.Seed("0001", "WAN Miniport (IP)", pnpCapabilities: null);
        fixture.Seed("0002", "Hyper-V Virtual Ethernet Adapter", pnpCapabilities: null);
        fixture.Seed("0003", "TAP-Windows Adapter V9", pnpCapabilities: null);
        fixture.Seed("0004", "SonicWall VPN Client Adapter", pnpCapabilities: null);
        fixture.Seed("0005", "Npcap Loopback Adapter", pnpCapabilities: null);
        fixture.Seed("0006", "Bluetooth Device (Personal Area Network)", pnpCapabilities: null);
        fixture.Seed("0007", "Microsoft Wi-Fi Direct Virtual Adapter", pnpCapabilities: null);

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
        AssessmentEvidence evidence = Assert.Single(assessment.Evidence);
        Assert.Contains("Intel(R) Ethernet", evidence.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Assess_SkipsAdaptersWithVirtualCharacteristicsFlag()
    {
        var fixture = new Fixture();
        // NCF_VIRTUAL without NCF_PHYSICAL: software adapter.
        fixture.Seed("0000", "Mystery Transport Adapter", pnpCapabilities: null, characteristics: 0x1);
        // NCF_PHYSICAL | NCF_HAS_UI: a physical adapter.
        fixture.Seed("0001", "Mystery Physical Adapter", pnpCapabilities: null, characteristics: 0x84);

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        AssessmentEvidence evidence = Assert.Single(assessment.Evidence);
        Assert.Contains("Physical", evidence.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Assess_SkipsNonEthernetOrWifiInterfaceTypes()
    {
        var fixture = new Fixture();
        fixture.Seed("0000", "Mystery Adapter", pnpCapabilities: null, ifType: 131);
        fixture.Seed("0001", "Ethernet Adapter", pnpCapabilities: 24, ifType: 6);

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
        AssessmentEvidence evidence = Assert.Single(assessment.Evidence);
        Assert.Contains("Ethernet Adapter", evidence.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Assess_IgnoresSubKeysWithoutDriverDesc()
    {
        var fixture = new Fixture();
        fixture.Registry.WriteValue(
            RegistryHive.LocalMachine,
            NetworkAdapterTestData.AdapterKey("Properties"),
            "PnPCapabilities",
            RegistryValueKind.DWord,
            RegistryValueCodec.ToRegistryDword(24));
        fixture.Seed("0000", "Intel(R) Ethernet Controller I225-V", pnpCapabilities: 24);

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
        Assert.Single(assessment.Evidence);
    }

    [Fact]
    public async Task Capture_CapturesExistenceAndExactDataPerAdapter()
    {
        var fixture = new Fixture();
        fixture.Seed("0000", "Intel(R) Ethernet Controller I225-V", pnpCapabilities: 16);
        fixture.Seed("0001", "Intel(R) Wi-Fi 6 AX201 160MHz", pnpCapabilities: null);
        int writesAfterSeeds = fixture.Registry.WriteCount;

        ChangeSet changeSet = await fixture.Optimization.CaptureAsync(fixture.Context);

        Assert.Equal("network-adapter-power", changeSet.OptimizationId);
        Assert.Equal(2, changeSet.Entries.Count);
        Assert.Equal(writesAfterSeeds, fixture.Registry.WriteCount);

        RegistrySnapshotEntry existing = Assert.Single(
            changeSet.Entries,
            entry => entry.SubKey == NetworkAdapterTestData.AdapterKey("0000"));
        Assert.True(existing.Existed);
        Assert.Equal(RegistryValueKind.DWord, existing.Kind);
        Assert.Equal("16", existing.SerializedData);
        Assert.Equal("PnPCapabilities", existing.ValueName);

        RegistrySnapshotEntry absent = Assert.Single(
            changeSet.Entries,
            entry => entry.SubKey == NetworkAdapterTestData.AdapterKey("0001"));
        Assert.False(absent.Existed);
        Assert.Null(absent.SerializedData);
    }

    [Fact]
    public async Task Capture_WhenNoPhysicalAdapters_Throws()
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Optimization.CaptureAsync(fixture.Context));
    }

    [Fact]
    public async Task Apply_WritesDword24ToEveryCapturedAdapter()
    {
        var fixture = new Fixture();
        fixture.Seed("0000", "Intel(R) Ethernet Controller I225-V", pnpCapabilities: null);
        fixture.Seed("0001", "Realtek PCIe GbE Family Controller", pnpCapabilities: 0);
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);
        int writesAfterCapture = fixture.Registry.WriteCount;

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, original);

        Assert.True(outcome.Success);
        Assert.Equal(writesAfterCapture + 2, fixture.Registry.WriteCount);
        Assert.Equal(2, outcome.Changes.Count);
        foreach (string instance in new[] { "0000", "0001" })
        {
            RegistryValue value = fixture.ReadPnp(instance)!;
            Assert.Equal(RegistryValueKind.DWord, value.Kind);
            Assert.Equal(24, fixture.ReadPnpValue(instance));
        }
    }

    [Fact]
    public async Task Apply_WhenAlready24_IsIdempotentAndDoesNotWrite()
    {
        var fixture = new Fixture();
        fixture.Seed("0000", "Intel(R) Ethernet Controller I225-V", pnpCapabilities: 24);
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);
        int writesAfterCapture = fixture.Registry.WriteCount;

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, original);

        Assert.True(outcome.Success);
        Assert.Equal(writesAfterCapture, fixture.Registry.WriteCount);
        Assert.Contains(
            outcome.Changes,
            change => change.Contains("already", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Apply_WhenBackupHasNoEntries_ReturnsFailure()
    {
        var fixture = new Fixture();
        var original = new ChangeSet
        {
            OptimizationId = "network-adapter-power",
            Entries = [],
        };

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, original);

        Assert.False(outcome.Success);
        Assert.Equal(0, fixture.Registry.WriteCount);
    }

    [Fact]
    public async Task Apply_WhenBackupHasForeignEntries_ReturnsFailure()
    {
        var fixture = new Fixture();
        var original = new ChangeSet
        {
            OptimizationId = "network-adapter-power",
            Entries =
            [
                new RegistrySnapshotEntry
                {
                    Hive = RegistryHive.LocalMachine,
                    SubKey = @"Software\Other\Key",
                    ValueName = "PnPCapabilities",
                    Existed = true,
                    Kind = RegistryValueKind.DWord,
                    SerializedData = "0",
                },
            ],
        };

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, original);

        Assert.False(outcome.Success);
        Assert.Equal(0, fixture.Registry.WriteCount);
    }

    [Fact]
    public async Task Restore_RestoresExactOriginalValuesAndDeletesAbsentOnes()
    {
        var fixture = new Fixture();
        fixture.Seed("0000", "Intel(R) Ethernet Controller I225-V", pnpCapabilities: 16);
        fixture.Seed("0001", "Realtek PCIe GbE Family Controller", pnpCapabilities: null);
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);

        OperationOutcome apply = await fixture.Optimization.ApplyAsync(fixture.Context, original);
        Assert.True(apply.Success);

        OperationOutcome restore = await fixture.Optimization.RestoreAsync(fixture.Context, original);

        Assert.True(restore.Success);
        Assert.True(restore.Verified);
        Assert.Equal(16, fixture.ReadPnpValue("0000"));
        Assert.Equal(RegistryValueKind.DWord, fixture.ReadPnp("0000")!.Kind);
        Assert.Null(fixture.ReadPnp("0001"));
        Assert.Equal(1, fixture.Registry.DeleteCount);
    }

    [Fact]
    public async Task Restore_RestoresTheExactOriginalRegistryKind()
    {
        var fixture = new Fixture();
        fixture.Seed(
            "0000",
            "Intel(R) Ethernet Controller I225-V",
            pnpCapabilities: 24,
            pnpKind: RegistryValueKind.String);
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);

        OperationOutcome apply = await fixture.Optimization.ApplyAsync(fixture.Context, original);
        Assert.True(apply.Success);
        Assert.Equal(RegistryValueKind.DWord, fixture.ReadPnp("0000")!.Kind);

        OperationOutcome restore = await fixture.Optimization.RestoreAsync(fixture.Context, original);

        Assert.True(restore.Success);
        Assert.True(restore.Verified);
        RegistryValue restored = fixture.ReadPnp("0000")!;
        Assert.Equal(RegistryValueKind.String, restored.Kind);
        Assert.Equal("24", restored.Data);
    }

    [Fact]
    public async Task Restore_WhenBackupHasNoEntries_ReturnsFailure()
    {
        var fixture = new Fixture();
        var original = new ChangeSet
        {
            OptimizationId = "network-adapter-power",
            Entries = [],
        };

        OperationOutcome outcome = await fixture.Optimization.RestoreAsync(fixture.Context, original);

        Assert.False(outcome.Success);
        Assert.Equal(0, fixture.Registry.DeleteCount);
    }

    [Fact]
    public async Task AllMethods_HonorPreCancelledToken()
    {
        var fixture = new Fixture();
        fixture.Seed("0000", "Intel(R) Ethernet Controller I225-V", pnpCapabilities: null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        CancellationToken token = cancellation.Token;
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Optimization.AssessAsync(fixture.Context, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Optimization.CaptureAsync(fixture.Context, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Optimization.ApplyAsync(fixture.Context, original, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Optimization.RestoreAsync(fixture.Context, original, token));
        Assert.Null(fixture.ReadPnp("0000"));
    }
}
