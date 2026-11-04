using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Optimizations;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests.Windows;

public sealed class WindowsGameModeOptimizationTests
{
    private const string Key = WindowsGameModeOptimization.RegistrySubKey;
    private const string Value = WindowsGameModeOptimization.ValueName;

    [Fact]
    public void Descriptor_IsStableAndFullyDescribed()
    {
        var optimization = new WindowsGameModeOptimization();

        WindowsTestHarness.AssertWellFormedDescriptor(
            optimization,
            "windows-game-mode",
            OptimizationCategory.Windows,
            RiskLevel.Safe,
            expectedRequiresElevation: false,
            new RegistryScope(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar"));

        Assert.Equal("Game Mode on", optimization.Descriptor.StateWhenApplied);
        Assert.Equal("Windows Game Mode", optimization.Descriptor.Name);
        Assert.DoesNotContain("fps", optimization.Descriptor.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Assess_MissingValue_ReportsAppliedByDefault()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        var optimization = new WindowsGameModeOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
        Assert.Contains("default", assessment.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(assessment.Explanation));
        AssessmentEvidence evidence = Assert.Single(assessment.Evidence);
        Assert.Equal(Value, evidence.Label);
        Assert.Contains("default", evidence.Value, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, registry.WriteCount);
        Assert.Equal(0, registry.DeleteCount);
    }

    [Fact]
    public async Task Assess_ExplicitOne_ReturnsApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.CurrentUser, Key, Value, 1);
        var optimization = new WindowsGameModeOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
        Assert.Equal("On", assessment.Headline);
        Assert.Contains("1", Assert.Single(assessment.Evidence).Value);
    }

    [Fact]
    public async Task Assess_ExplicitZero_ReturnsNotApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.CurrentUser, Key, Value, 0);
        var optimization = new WindowsGameModeOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Equal("Off", assessment.Headline);
        Assert.False(string.IsNullOrWhiteSpace(assessment.Explanation));
        Assert.Contains("0", Assert.Single(assessment.Evidence).Value);
    }

    [Fact]
    public async Task Assess_UnrecognizedDword_ReturnsNotApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.CurrentUser, Key, Value, 7);
        var optimization = new WindowsGameModeOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains("7", assessment.Headline);
    }

    [Fact]
    public async Task Assess_UnexpectedValueKind_ReturnsNotApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetString(RegistryHive.CurrentUser, Key, Value, "1");
        var optimization = new WindowsGameModeOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains("String", Assert.Single(assessment.Evidence).Value);
        Assert.Contains("DWORD", assessment.Explanation);
    }

    [Fact]
    public async Task Capture_PresentValue_RecordsKindAndData()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.CurrentUser, Key, Value, 0);
        var optimization = new WindowsGameModeOptimization();

        ChangeSet original = await optimization.CaptureAsync(context);

        RegistrySnapshotEntry entry = Assert.Single(original.Entries);
        Assert.Equal("windows-game-mode", original.OptimizationId);
        Assert.Equal(RegistryHive.CurrentUser, entry.Hive);
        Assert.Equal(Key, entry.SubKey);
        Assert.Equal(Value, entry.ValueName);
        Assert.True(entry.Existed);
        Assert.Equal(RegistryValueKind.DWord, entry.Kind);
        Assert.Equal("0", entry.SerializedData);
    }

    [Fact]
    public async Task Capture_MissingValue_RecordsAbsentEntry()
    {
        OptimizationContext context = WindowsTestHarness.CreateContext(out _);
        var optimization = new WindowsGameModeOptimization();

        ChangeSet original = await optimization.CaptureAsync(context);

        RegistrySnapshotEntry entry = Assert.Single(original.Entries);
        Assert.False(entry.Existed);
        Assert.Equal(RegistryValueKind.Unknown, entry.Kind);
        Assert.Null(entry.SerializedData);
    }

    [Fact]
    public async Task Apply_WritesDwordOne()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        var optimization = new WindowsGameModeOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);

        OperationOutcome outcome = await optimization.ApplyAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Single(outcome.Changes);
        Assert.Equal(1, WindowsTestHarness.ReadDword(registry, RegistryHive.CurrentUser, Key, Value));
    }

    [Fact]
    public async Task Apply_WhenCancelled_WritesNothing()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        var optimization = new WindowsGameModeOptimization();
        ChangeSet original = new() { OptimizationId = "windows-game-mode" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => optimization.ApplyAsync(context, original, cts.Token));

        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public async Task Restore_RestoresExplicitZero()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.CurrentUser, Key, Value, 0);
        var optimization = new WindowsGameModeOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);
        await optimization.ApplyAsync(context, original);

        OperationOutcome outcome = await optimization.RestoreAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Equal(0, WindowsTestHarness.ReadDword(registry, RegistryHive.CurrentUser, Key, Value));
    }

    [Fact]
    public async Task Restore_DeletesValueThatWasOriginallyAbsent()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        var optimization = new WindowsGameModeOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);
        await optimization.ApplyAsync(context, original);
        Assert.NotNull(registry.ReadValue(RegistryHive.CurrentUser, Key, Value));

        OperationOutcome outcome = await optimization.RestoreAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Null(registry.ReadValue(RegistryHive.CurrentUser, Key, Value));
        Assert.Equal(1, registry.DeleteCount);
    }
}
