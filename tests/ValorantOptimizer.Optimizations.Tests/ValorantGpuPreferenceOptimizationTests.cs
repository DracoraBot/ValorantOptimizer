using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Diagnostics;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests;

public sealed class ValorantGpuPreferenceOptimizationTests
{
    private const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string LauncherPath = @"C:\Riot Games\VALORANT\live\VALORANT.exe";
    private const string ShippingPath =
        @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe";

    private sealed class FakeValorantLocator : IValorantLocator
    {
        public FakeValorantLocator(ValorantInstallation installation) => Installation = installation;

        public ValorantInstallation Installation { get; set; }

        public int Calls { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task<ValorantInstallation> LocateAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(Installation);
        }
    }

    private sealed class Fixture
    {
        public Fixture(string? launcher = LauncherPath, string? shipping = ShippingPath)
        {
            Locator = new FakeValorantLocator(new ValorantInstallation
            {
                ExecutablePath = launcher,
                ShippingExecutablePath = shipping,
            });
            Optimization = new ValorantGpuPreferenceOptimization(Locator);
            Context = new OptimizationContext(Registry, new FakeProcessRunner(), isElevated: false);
        }

        public FakeRegistryStore Registry { get; } = new();

        public FakeValorantLocator Locator { get; }

        public OptimizationContext Context { get; }

        public ValorantGpuPreferenceOptimization Optimization { get; }

        public void Seed(string executablePath, string data, RegistryValueKind kind = RegistryValueKind.String) =>
            Registry.WriteValue(RegistryHive.CurrentUser, Key, executablePath, kind, data);

        public RegistryValue? Read(string executablePath) =>
            Registry.ReadValue(RegistryHive.CurrentUser, Key, executablePath);

        public string? ReadString(string executablePath) => Read(executablePath)?.Data as string;
    }

    [Fact]
    public void Descriptor_HasExpectedIdentityAndScope()
    {
        var fixture = new Fixture();

        OptimizationDescriptor descriptor = fixture.Optimization.Descriptor;

        Assert.Equal("valorant-gpu-preference", descriptor.Id);
        Assert.Equal(OptimizationCategory.Valorant, descriptor.Category);
        Assert.Equal(RiskLevel.Safe, descriptor.Risk);
        Assert.False(descriptor.RequiresElevation);
        Assert.Equal(RestartRequirement.None, descriptor.Restart);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Summary));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.WhatItChanges));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Why));
        Assert.Contains(descriptor.Notes, note => note.Contains("global", StringComparison.OrdinalIgnoreCase));

        RegistryScope scope = Assert.Single(descriptor.ManagedScopes);
        Assert.Equal(RegistryHive.CurrentUser, scope.Hive);
        Assert.Equal(Key, scope.SubKey);
    }

    [Fact]
    public void Constructor_RejectsNullLocator()
    {
        Assert.Throws<ArgumentNullException>(() => new ValorantGpuPreferenceOptimization(null!));
    }

    [Fact]
    public async Task Assess_WhenNoExecutableIsLocated_IsUnsupportedAndDoesNotWrite()
    {
        var fixture = new Fixture(launcher: null, shipping: null);

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Unsupported, assessment.State);
        Assert.False(string.IsNullOrWhiteSpace(assessment.BlockedReason));
        Assert.Contains("VALORANT", assessment.BlockedReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Registry.WriteCount);
        Assert.Empty(fixture.Registry.GetValueNames(RegistryHive.CurrentUser, Key));
    }

    [Fact]
    public async Task Assess_LauncherOnly_TargetsLauncherAndWarnsGameBinaryMissing()
    {
        var fixture = new Fixture(launcher: LauncherPath, shipping: null);

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains(assessment.Evidence, item => item.Label == "VALORANT.exe");
        Assert.Contains(
            assessment.Evidence,
            item => item.Label == "VALORANT-Win64-Shipping.exe" &&
                    item.Value.Contains("Not found", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("game binary", assessment.Explanation!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Registry.WriteCount);
    }

    [Fact]
    public async Task Assess_ShippingOnly_TargetsGameBinaryAndWarnsLauncherMissing()
    {
        var fixture = new Fixture(launcher: null, shipping: ShippingPath);

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains(assessment.Evidence, item => item.Label == "VALORANT-Win64-Shipping.exe");
        Assert.Contains(
            assessment.Evidence,
            item => item.Label == "VALORANT.exe" &&
                    item.Value.Contains("Not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Assess_BothFoundWithoutPreference_IsNotAppliedWithEvidencePerExecutable()
    {
        var fixture = new Fixture();

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Equal(2, assessment.Evidence.Count);
        Assert.Contains(assessment.Evidence, item => item.Label == "VALORANT.exe");
        Assert.Contains(assessment.Evidence, item => item.Label == "VALORANT-Win64-Shipping.exe");
        Assert.Contains("not set", assessment.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Assess_IsAppliedOnlyWhenEveryExecutableHasHighPerformance()
    {
        var fixture = new Fixture();
        fixture.Seed(LauncherPath, "GpuPreference=2;");

        OptimizationAssessment first = await fixture.Optimization.AssessAsync(fixture.Context);
        Assert.Equal(OptimizationState.NotApplied, first.State);

        fixture.Seed(ShippingPath, "GpuPreference=2;");

        OptimizationAssessment second = await fixture.Optimization.AssessAsync(fixture.Context);
        Assert.Equal(OptimizationState.Applied, second.State);
        Assert.Contains(second.Evidence, item => item.Label == "VALORANT.exe");
        Assert.Contains(second.Evidence, item => item.Label == "VALORANT-Win64-Shipping.exe");
    }

    [Fact]
    public async Task Assess_ParsesCompoundDataAndIgnoresUnrelatedPairs()
    {
        var fixture = new Fixture();
        fixture.Seed(LauncherPath, "GpuPreference=2;SwapEffectUpgradeEnable=1;");
        fixture.Seed(ShippingPath, "SwapEffectUpgradeEnable=1;GpuPreference=2;");

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
    }

    [Fact]
    public async Task Assess_PowerSavingOrWrongKind_IsNotApplied()
    {
        var fixture = new Fixture();
        fixture.Seed(LauncherPath, "GpuPreference=1;");
        fixture.Seed(ShippingPath, "GpuPreference=2;", RegistryValueKind.DWord);

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Equal(2, fixture.Registry.WriteCount); // only the two seeds wrote
    }

    [Fact]
    public async Task Assess_TreatsPreferenceWithoutTrailingSemicolonAsApplied()
    {
        var fixture = new Fixture();
        fixture.Seed(LauncherPath, "GpuPreference=2");
        fixture.Seed(ShippingPath, "GpuPreference=2");

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
    }

    [Fact]
    public async Task Apply_NormalizesMissingTrailingSemicolonAndKeepsPairSpacing()
    {
        var fixture = new Fixture();
        fixture.Seed(LauncherPath, "GpuPreference=1");
        fixture.Seed(ShippingPath, " SwapEffectUpgradeEnable = 1 ");
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, original);

        Assert.True(outcome.Success);
        Assert.Equal("GpuPreference=2;", fixture.ReadString(LauncherPath));
        Assert.Equal(" SwapEffectUpgradeEnable = 1 ;GpuPreference=2;", fixture.ReadString(ShippingPath));
    }

    [Fact]
    public async Task Capture_CapturesExistenceAndExactDataForEachTarget()
    {
        var fixture = new Fixture();
        fixture.Seed(LauncherPath, "SwapEffectUpgradeEnable=1;");
        int writesAfterSeed = fixture.Registry.WriteCount;

        ChangeSet changeSet = await fixture.Optimization.CaptureAsync(fixture.Context);

        Assert.Equal("valorant-gpu-preference", changeSet.OptimizationId);
        Assert.Equal(2, changeSet.Entries.Count);
        Assert.Equal(writesAfterSeed, fixture.Registry.WriteCount);

        RegistrySnapshotEntry launcher = Assert.Single(
            changeSet.Entries,
            entry => entry.ValueName == LauncherPath);
        Assert.True(launcher.Existed);
        Assert.Equal(RegistryValueKind.String, launcher.Kind);
        Assert.Equal("SwapEffectUpgradeEnable=1;", launcher.SerializedData);
        Assert.Equal(RegistryHive.CurrentUser, launcher.Hive);
        Assert.Equal(Key, launcher.SubKey);

        RegistrySnapshotEntry shipping = Assert.Single(
            changeSet.Entries,
            entry => entry.ValueName == ShippingPath);
        Assert.False(shipping.Existed);
        Assert.Null(shipping.SerializedData);
    }

    [Fact]
    public async Task Capture_WhenNoExecutableIsLocated_Throws()
    {
        var fixture = new Fixture(launcher: null, shipping: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Optimization.CaptureAsync(fixture.Context));
    }

    [Fact]
    public async Task Apply_WritesHighPerformanceForBothExecutablesWithoutReCapturing()
    {
        var fixture = new Fixture();
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);
        int locatorCallsAfterCapture = fixture.Locator.Calls;

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, original);

        Assert.True(outcome.Success);
        Assert.Equal(locatorCallsAfterCapture, fixture.Locator.Calls);
        Assert.Equal(2, fixture.Registry.WriteCount);
        Assert.Equal("GpuPreference=2;", fixture.ReadString(LauncherPath));
        Assert.Equal("GpuPreference=2;", fixture.ReadString(ShippingPath));
        Assert.Equal(RegistryValueKind.String, fixture.Read(LauncherPath)!.Kind);
        Assert.Equal(2, outcome.Changes.Count);
        Assert.Empty(outcome.Warnings);
        Assert.Contains("no reboot", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_PreservesUnrelatedPairsAndReplacesExistingPreference()
    {
        var fixture = new Fixture();
        fixture.Seed(LauncherPath, "SwapEffectUpgradeEnable=1;");
        fixture.Seed(ShippingPath, "GpuPreference=1;SwapEffectUpgradeEnable=1;");
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, original);

        Assert.True(outcome.Success);
        Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=2;", fixture.ReadString(LauncherPath));
        Assert.Equal("GpuPreference=2;SwapEffectUpgradeEnable=1;", fixture.ReadString(ShippingPath));
    }

    [Fact]
    public async Task Apply_MergesWithLiveDataWrittenAfterCapture()
    {
        var fixture = new Fixture();
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);

        // Simulate another tool/Windows writing an unrelated pair between capture and apply.
        fixture.Seed(LauncherPath, "SwapEffectUpgradeEnable=1;");

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, original);

        Assert.True(outcome.Success);
        Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=2;", fixture.ReadString(LauncherPath));
        Assert.Equal("GpuPreference=2;", fixture.ReadString(ShippingPath));
    }

    [Fact]
    public async Task Apply_LauncherOnly_ConfiguresOneExecutableAndWarnsAboutGameBinary()
    {
        var fixture = new Fixture(launcher: LauncherPath, shipping: null);
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, original);

        Assert.True(outcome.Success);
        Assert.Equal("GpuPreference=2;", fixture.ReadString(LauncherPath));
        Assert.Equal(1, fixture.Registry.WriteCount);
        Assert.Contains(
            outcome.Warnings,
            warning => warning.Contains("VALORANT-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Apply_WhenBackupHasNoEntries_ReturnsFailure()
    {
        var fixture = new Fixture();
        var original = new ChangeSet
        {
            OptimizationId = "valorant-gpu-preference",
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
            OptimizationId = "valorant-gpu-preference",
            Entries =
            [
                new RegistrySnapshotEntry
                {
                    Hive = RegistryHive.CurrentUser,
                    SubKey = @"Software\Other\Key",
                    ValueName = "Value",
                    Existed = true,
                    Kind = RegistryValueKind.String,
                    SerializedData = "1",
                },
            ],
        };

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, original);

        Assert.False(outcome.Success);
        Assert.Equal(0, fixture.Registry.WriteCount);
    }

    [Fact]
    public async Task Restore_RestoresExactOriginalCompoundStrings()
    {
        var fixture = new Fixture();
        fixture.Seed(LauncherPath, "GpuPreference=1;SwapEffectUpgradeEnable=1;");
        fixture.Seed(ShippingPath, "GpuPreference=0;");
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);

        OperationOutcome apply = await fixture.Optimization.ApplyAsync(fixture.Context, original);
        Assert.True(apply.Success);

        OperationOutcome restore = await fixture.Optimization.RestoreAsync(fixture.Context, original);

        Assert.True(restore.Success);
        Assert.True(restore.Verified);
        Assert.Equal("GpuPreference=1;SwapEffectUpgradeEnable=1;", fixture.ReadString(LauncherPath));
        Assert.Equal("GpuPreference=0;", fixture.ReadString(ShippingPath));
        Assert.Equal(RegistryValueKind.String, fixture.Read(LauncherPath)!.Kind);
        Assert.Equal(RegistryValueKind.String, fixture.Read(ShippingPath)!.Kind);
    }

    [Fact]
    public async Task Restore_DeletesValuesThatDidNotExistBeforeCapture()
    {
        var fixture = new Fixture();
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);

        OperationOutcome apply = await fixture.Optimization.ApplyAsync(fixture.Context, original);
        Assert.True(apply.Success);
        Assert.NotNull(fixture.Read(LauncherPath));
        Assert.NotNull(fixture.Read(ShippingPath));

        OperationOutcome restore = await fixture.Optimization.RestoreAsync(fixture.Context, original);

        Assert.True(restore.Success);
        Assert.True(restore.Verified);
        Assert.Null(fixture.Read(LauncherPath));
        Assert.Null(fixture.Read(ShippingPath));
        Assert.Equal(2, fixture.Registry.DeleteCount);
    }

    [Fact]
    public async Task Restore_WhenBackupHasNoEntries_ReturnsFailure()
    {
        var fixture = new Fixture();
        var original = new ChangeSet
        {
            OptimizationId = "valorant-gpu-preference",
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
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        ChangeSet empty = new() { OptimizationId = "valorant-gpu-preference", Entries = [] };
        CancellationToken token = cancellation.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Optimization.AssessAsync(fixture.Context, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Optimization.CaptureAsync(fixture.Context, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Optimization.ApplyAsync(fixture.Context, empty, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Optimization.RestoreAsync(fixture.Context, empty, token));
    }
}
