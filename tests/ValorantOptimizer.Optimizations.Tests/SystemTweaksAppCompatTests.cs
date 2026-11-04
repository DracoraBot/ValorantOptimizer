using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Diagnostics;
using ValorantOptimizer.Optimizations;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests.SystemTweaks;

public sealed class ValorantAppCompatTests
{
    private const string Key = ValorantAppCompatOptimization.LayersKey;
    private const string LauncherPath = @"C:\Riot Games\VALORANT\live\VALORANT.exe";
    private const string ShippingPath =
        @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe";

    private sealed class Fixture
    {
        public Fixture(string? launcher = LauncherPath, string? shipping = ShippingPath)
        {
            Locator = new FakeValorantLocator(new ValorantInstallation
            {
                ExecutablePath = launcher,
                ShippingExecutablePath = shipping,
            });
            Optimization = new ValorantAppCompatOptimization(Locator);
            Context = new OptimizationContext(Registry, new FakeProcessRunner(), isElevated: false);
        }

        public FakeRegistryStore Registry { get; } = new();

        public FakeValorantLocator Locator { get; }

        public OptimizationContext Context { get; }

        public ValorantAppCompatOptimization Optimization { get; }

        public void Seed(string executablePath, string data) =>
            Registry.WriteValue(RegistryHive.CurrentUser, Key, executablePath, RegistryValueKind.String, data);

        public RegistryValue? Read(string executablePath) =>
            Registry.ReadValue(RegistryHive.CurrentUser, Key, executablePath);

        public string? ReadString(string executablePath) => Read(executablePath)?.Data as string;
    }

    private static ChangeSet OutOfScopeChangeSet() => new()
    {
        OptimizationId = "valorant-appcompat",
        Entries =
        [
            new RegistrySnapshotEntry
            {
                Hive = RegistryHive.CurrentUser,
                SubKey = @"Software\Microsoft\Windows\CurrentVersion\Unrelated",
                ValueName = LauncherPath,
                Existed = false,
            },
        ],
    };

    [Fact]
    public void Descriptor_IsStableAndFullyDescribed()
    {
        var fixture = new Fixture();

        SystemTweaksTestHarness.AssertWellFormedDescriptor(
            fixture.Optimization,
            "valorant-appcompat",
            OptimizationCategory.Valorant,
            RiskLevel.Safe,
            expectedRequiresElevation: false,
            RestartRequirement.None,
            new RegistryScope(RegistryHive.CurrentUser, Key));

        Assert.Equal("Windowed-mode flag on", fixture.Optimization.Descriptor.StateWhenApplied);
        Assert.Contains(
            "DISABLEDXMAXIMIZEDWINDOWEDMODE",
            fixture.Optimization.Descriptor.WhatItChanges,
            StringComparison.Ordinal);
        Assert.DoesNotContain("fps", fixture.Optimization.Descriptor.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fps", fixture.Optimization.Descriptor.Why, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            fixture.Optimization.Descriptor.Notes,
            note => note.Contains("preserved", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Constructor_RejectsNullLocator()
    {
        Assert.Throws<ArgumentNullException>(() => new ValorantAppCompatOptimization(null!));
    }

    [Fact]
    public async Task Assess_NoExecutableLocated_IsUnsupportedAndDoesNotWrite()
    {
        var fixture = new Fixture(launcher: null, shipping: null);

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Unsupported, assessment.State);
        Assert.Contains("No VALORANT executable", assessment.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(assessment.IsActionable);
        Assert.Equal(0, fixture.Registry.WriteCount);
    }

    [Fact]
    public async Task Assess_BothExecutablesFlagged_ReturnsApplied()
    {
        var fixture = new Fixture();
        fixture.Seed(LauncherPath, "~ DISABLEDXMAXIMIZEDWINDOWEDMODE");
        fixture.Seed(ShippingPath, "~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE");

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
        Assert.Equal(2, assessment.Evidence.Count);
        Assert.All(
            assessment.Evidence,
            evidence => Assert.Contains("Flag present", evidence.Value, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Assess_FlagMissing_ReturnsNotApplied()
    {
        var fixture = new Fixture();
        fixture.Seed(LauncherPath, "~ HIGHDPIAWARE");
        fixture.Seed(ShippingPath, "~ DISABLEDXMAXIMIZEDWINDOWEDMODE");

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.False(string.IsNullOrWhiteSpace(assessment.Explanation));
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "VALORANT.exe" &&
                        evidence.Value.Contains("Flag absent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Assess_LauncherOnly_ExplainsGameBinaryMissing()
    {
        var fixture = new Fixture(shipping: null);

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == "VALORANT-Win64-Shipping.exe" &&
                        evidence.Value.Contains("Not found", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("game binary", assessment.Explanation!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssessAndCapture_DoNotWrite()
    {
        var fixture = new Fixture();
        fixture.Seed(LauncherPath, "~ HIGHDPIAWARE");
        int writesAfterSeeding = fixture.Registry.WriteCount;

        await fixture.Optimization.AssessAsync(fixture.Context);
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);

        Assert.Equal(writesAfterSeeding, fixture.Registry.WriteCount);
        Assert.Equal(0, fixture.Registry.DeleteCount);
        Assert.Equal(2, fixture.Locator.Calls); // one locate for Assess, one for Capture
        Assert.Equal(2, original.Entries.Count);
    }

    [Fact]
    public async Task Capture_RecordsBothTargetsWithExactData()
    {
        var fixture = new Fixture();
        fixture.Seed(LauncherPath, "~ HIGHDPIAWARE");
        // Shipping binary value is deliberately absent -> Existed=false.

        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);

        Assert.Equal("valorant-appcompat", original.OptimizationId);
        Assert.Equal(2, original.Entries.Count);

        RegistrySnapshotEntry launcher = original.Entries[0];
        Assert.Equal(LauncherPath, launcher.ValueName);
        Assert.True(launcher.Existed);
        Assert.Equal(RegistryValueKind.String, launcher.Kind);
        Assert.Equal("~ HIGHDPIAWARE", launcher.SerializedData);

        RegistrySnapshotEntry shipping = original.Entries[1];
        Assert.Equal(ShippingPath, shipping.ValueName);
        Assert.False(shipping.Existed);
        Assert.Null(shipping.SerializedData);
    }

    [Fact]
    public async Task Capture_NoExecutableLocated_Throws()
    {
        var fixture = new Fixture(launcher: null, shipping: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Optimization.CaptureAsync(fixture.Context));
    }

    [Fact]
    public async Task Apply_MergesFlagAndPreservesUnrelatedTokens()
    {
        var fixture = new Fixture();
        fixture.Seed(LauncherPath, "~ HIGHDPIAWARE RUNASADMIN");
        fixture.Seed(ShippingPath, "~ DISABLEDXMAXIMIZEDWINDOWEDMODE");
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, original);

        Assert.True(outcome.Success);
        Assert.Equal(
            "~ HIGHDPIAWARE RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE",
            fixture.ReadString(LauncherPath));
        Assert.Equal(
            "~ DISABLEDXMAXIMIZEDWINDOWEDMODE",
            fixture.ReadString(ShippingPath));
        Assert.Equal(2, outcome.Changes.Count);
        Assert.Empty(outcome.Warnings);
    }

    [Fact]
    public async Task Apply_AddsTildeMarkerWhenExistingDataHasNone()
    {
        var fixture = new Fixture();
        fixture.Seed(LauncherPath, "HIGHDPIAWARE");
        fixture.Seed(ShippingPath, "~ DISABLEDXMAXIMIZEDWINDOWEDMODE");
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);

        await fixture.Optimization.ApplyAsync(fixture.Context, original);

        Assert.Equal(
            "~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE",
            fixture.ReadString(LauncherPath));
    }

    [Fact]
    public async Task Apply_LauncherOnly_AddsGameBinaryWarning()
    {
        var fixture = new Fixture(shipping: null);
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, original);

        Assert.True(outcome.Success);
        Assert.Equal(
            "~ DISABLEDXMAXIMIZEDWINDOWEDMODE",
            fixture.ReadString(LauncherPath));
        string warning = Assert.Single(outcome.Warnings);
        Assert.Contains("game binary", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_WhenFlagAlreadyPresent_DoesNotRewrite()
    {
        var fixture = new Fixture();
        fixture.Seed(LauncherPath, "~ DISABLEDXMAXIMIZEDWINDOWEDMODE");
        fixture.Seed(ShippingPath, "~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE");
        int writesAfterSeeding = fixture.Registry.WriteCount;
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, original);

        Assert.True(outcome.Success);
        Assert.Equal(writesAfterSeeding, fixture.Registry.WriteCount);
        Assert.Equal(2, outcome.Changes.Count);
        Assert.All(
            outcome.Changes,
            change => Assert.Contains("already had", change, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Apply_WhenCancelled_WritesNothing()
    {
        var fixture = new Fixture();
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Optimization.ApplyAsync(fixture.Context, original, cts.Token));

        Assert.Equal(0, fixture.Registry.WriteCount);
    }

    [Fact]
    public async Task Apply_RejectsCapturedEntriesOutsideScope()
    {
        var fixture = new Fixture();

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, OutOfScopeChangeSet());

        Assert.False(outcome.Success);
        Assert.Contains("outside", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Registry.WriteCount);
    }

    [Fact]
    public async Task Apply_NoCapturedEntries_Fails()
    {
        var fixture = new Fixture();
        ChangeSet empty = new() { OptimizationId = "valorant-appcompat" };

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, empty);

        Assert.False(outcome.Success);
        Assert.Equal(0, fixture.Registry.WriteCount);
    }

    [Fact]
    public async Task Restore_RestoresExactOriginalsAndDeletesValuesThatWereAbsent()
    {
        var fixture = new Fixture();
        fixture.Seed(LauncherPath, "~ HIGHDPIAWARE");
        // Shipping value absent on purpose.
        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);
        await fixture.Optimization.ApplyAsync(fixture.Context, original);

        OperationOutcome outcome = await fixture.Optimization.RestoreAsync(fixture.Context, original);

        Assert.True(outcome.Success);
        Assert.Equal("~ HIGHDPIAWARE", fixture.ReadString(LauncherPath));
        Assert.Null(fixture.Read(ShippingPath));
        Assert.Equal(1, fixture.Registry.DeleteCount);
    }

    [Fact]
    public async Task Restore_RejectsCapturedEntriesOutsideScope()
    {
        var fixture = new Fixture();

        OperationOutcome outcome = await fixture.Optimization.RestoreAsync(fixture.Context, OutOfScopeChangeSet());

        Assert.False(outcome.Success);
        Assert.Contains("outside", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Registry.WriteCount);
        Assert.Equal(0, fixture.Registry.DeleteCount);
    }

    [Fact]
    public async Task Restore_NoCapturedEntries_Fails()
    {
        var fixture = new Fixture();
        ChangeSet empty = new() { OptimizationId = "valorant-appcompat" };

        OperationOutcome outcome = await fixture.Optimization.RestoreAsync(fixture.Context, empty);

        Assert.False(outcome.Success);
    }
}
