using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Optimizations;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests.Windows;

public sealed class GameBarCaptureOptimizationTests
{
    private const string GameDvrKey = GameBarCaptureOptimization.GameDvrSubKey;
    private const string GameDvrValue = GameBarCaptureOptimization.GameDvrValueName;
    private const string AppCaptureKey = GameBarCaptureOptimization.AppCaptureSubKey;
    private const string AppCaptureValue = GameBarCaptureOptimization.AppCaptureValueName;

    [Fact]
    public void Descriptor_IsStableAndFullyDescribed()
    {
        var optimization = new GameBarCaptureOptimization();

        WindowsTestHarness.AssertWellFormedDescriptor(
            optimization,
            "game-bar-capture",
            OptimizationCategory.Windows,
            RiskLevel.Safe,
            expectedRequiresElevation: false,
            new RegistryScope(RegistryHive.CurrentUser, @"System\GameConfigStore"),
            new RegistryScope(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR"));

        Assert.Equal("Background capture off", optimization.Descriptor.StateWhenApplied);
        Assert.Equal("Xbox Game Bar background capture", optimization.Descriptor.Name);
        Assert.Contains(
            optimization.Descriptor.Notes,
            note => note.Contains("manually", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("fps", optimization.Descriptor.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Assess_BothValuesZero_ReturnsApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.CurrentUser, GameDvrKey, GameDvrValue, 0);
        registry.SetDword(RegistryHive.CurrentUser, AppCaptureKey, AppCaptureValue, 0);
        var optimization = new GameBarCaptureOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
        Assert.Equal("Background capture off", assessment.Headline);
        Assert.Equal(2, assessment.Evidence.Count);
        Assert.All(assessment.Evidence, evidence => Assert.StartsWith("0", evidence.Value));
    }

    [Fact]
    public async Task Assess_BothValuesMissing_ReturnsNotAppliedUsingWindowsDefaults()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        var optimization = new GameBarCaptureOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains("default", assessment.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, assessment.Evidence.Count);
        Assert.All(
            assessment.Evidence,
            evidence => Assert.Contains("default", evidence.Value, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, registry.WriteCount);
        Assert.Equal(0, registry.DeleteCount);
    }

    [Fact]
    public async Task Assess_BothValuesEnabled_ReturnsNotApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.CurrentUser, GameDvrKey, GameDvrValue, 1);
        registry.SetDword(RegistryHive.CurrentUser, AppCaptureKey, AppCaptureValue, 1);
        var optimization = new GameBarCaptureOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Equal("Background capture on", assessment.Headline);
    }

    [Fact]
    public async Task Assess_PartialConfiguration_ReportsPartialState()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.CurrentUser, GameDvrKey, GameDvrValue, 0);
        registry.SetDword(RegistryHive.CurrentUser, AppCaptureKey, AppCaptureValue, 1);
        var optimization = new GameBarCaptureOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains("partially", assessment.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(assessment.Explanation));
    }

    [Fact]
    public async Task Assess_OneDisabledOtherMissing_ReturnsNotApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.CurrentUser, GameDvrKey, GameDvrValue, 0);
        var optimization = new GameBarCaptureOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Equal(2, assessment.Evidence.Count);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Value.Contains("default", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Capture_RecordsBothEntriesIncludingAbsentOnes()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.CurrentUser, GameDvrKey, GameDvrValue, 1);
        // AppCaptureEnabled is absent on purpose.
        var optimization = new GameBarCaptureOptimization();

        ChangeSet original = await optimization.CaptureAsync(context);

        Assert.Equal("game-bar-capture", original.OptimizationId);
        Assert.Equal(2, original.Entries.Count);

        RegistrySnapshotEntry gameDvr = original.Entries[0];
        Assert.Equal(GameDvrKey, gameDvr.SubKey);
        Assert.Equal(GameDvrValue, gameDvr.ValueName);
        Assert.True(gameDvr.Existed);
        Assert.Equal(RegistryValueKind.DWord, gameDvr.Kind);
        Assert.Equal("1", gameDvr.SerializedData);

        RegistrySnapshotEntry appCapture = original.Entries[1];
        Assert.Equal(AppCaptureKey, appCapture.SubKey);
        Assert.Equal(AppCaptureValue, appCapture.ValueName);
        Assert.False(appCapture.Existed);
        Assert.Null(appCapture.SerializedData);
    }

    [Fact]
    public async Task Apply_WritesZeroDwordToBothValues()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.CurrentUser, GameDvrKey, GameDvrValue, 1);
        registry.SetDword(RegistryHive.CurrentUser, AppCaptureKey, AppCaptureValue, 1);
        var optimization = new GameBarCaptureOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);

        OperationOutcome outcome = await optimization.ApplyAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Equal(2, outcome.Changes.Count);
        Assert.Equal(0, WindowsTestHarness.ReadDword(registry, RegistryHive.CurrentUser, GameDvrKey, GameDvrValue));
        Assert.Equal(0, WindowsTestHarness.ReadDword(registry, RegistryHive.CurrentUser, AppCaptureKey, AppCaptureValue));
    }

    [Fact]
    public async Task Apply_WhenCancelled_WritesNothing()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        var optimization = new GameBarCaptureOptimization();
        ChangeSet original = new() { OptimizationId = "game-bar-capture" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => optimization.ApplyAsync(context, original, cts.Token));

        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public async Task Restore_ReproducesOriginalsAndDeletesValuesThatWereAbsent()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.CurrentUser, GameDvrKey, GameDvrValue, 1);
        // AppCaptureEnabled is absent on purpose.
        var optimization = new GameBarCaptureOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);
        await optimization.ApplyAsync(context, original);

        OperationOutcome outcome = await optimization.RestoreAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Equal(1, WindowsTestHarness.ReadDword(registry, RegistryHive.CurrentUser, GameDvrKey, GameDvrValue));
        Assert.Null(registry.ReadValue(RegistryHive.CurrentUser, AppCaptureKey, AppCaptureValue));
        Assert.Equal(1, registry.DeleteCount);
    }
}
