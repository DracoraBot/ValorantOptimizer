using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Optimizations;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests.SystemTweaks;

public sealed class WindowsBackgroundAppsTests
{
    private const string Key = WindowsBackgroundAppsOptimization.RegistrySubKey;
    private const string Value = WindowsBackgroundAppsOptimization.ValueName;

    [Fact]
    public void Descriptor_IsStableAndFullyDescribed()
    {
        var optimization = new WindowsBackgroundAppsOptimization();

        SystemTweaksTestHarness.AssertWellFormedDescriptor(
            optimization,
            "windows-background-apps",
            OptimizationCategory.Windows,
            RiskLevel.Safe,
            expectedRequiresElevation: false,
            RestartRequirement.None,
            new RegistryScope(RegistryHive.CurrentUser, Key));

        Assert.Equal("Background apps off", optimization.Descriptor.StateWhenApplied);
        Assert.DoesNotContain("fps", optimization.Descriptor.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fps", optimization.Descriptor.Why, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            optimization.Descriptor.Notes,
            note => note.Contains("per-user", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Assess_DwordOne_ReturnsApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.CurrentUser, Key, Value, 1);
        var optimization = new WindowsBackgroundAppsOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
        Assert.Equal("Background apps off", assessment.Headline);
        AssessmentEvidence evidence = Assert.Single(assessment.Evidence);
        Assert.Equal(Value, evidence.Label);
        Assert.Contains("disabled", evidence.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Assess_DwordZero_ReturnsNotApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.CurrentUser, Key, Value, 0);
        var optimization = new WindowsBackgroundAppsOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Equal("Background apps allowed", assessment.Headline);
        Assert.False(string.IsNullOrWhiteSpace(assessment.Explanation));
    }

    [Fact]
    public async Task Assess_MissingValue_ReturnsDefaultNotApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        var optimization = new WindowsBackgroundAppsOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains("default", assessment.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("default", Assert.Single(assessment.Evidence).Value, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public async Task Assess_UnexpectedValueKind_ReturnsNotApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        registry.SetString(RegistryHive.CurrentUser, Key, Value, "1");
        var optimization = new WindowsBackgroundAppsOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains("String", Assert.Single(assessment.Evidence).Value);
        Assert.Contains("DWORD", assessment.Explanation);
    }

    [Fact]
    public async Task AssessAndCapture_DoNotWrite()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.CurrentUser, Key, Value, 0);
        int writesAfterSeeding = registry.WriteCount;
        var optimization = new WindowsBackgroundAppsOptimization();

        await optimization.AssessAsync(context);
        ChangeSet original = await optimization.CaptureAsync(context);

        Assert.Equal(writesAfterSeeding, registry.WriteCount);
        Assert.Equal(0, registry.DeleteCount);
        RegistrySnapshotEntry entry = Assert.Single(original.Entries);
        Assert.True(entry.Existed);
        Assert.Equal(RegistryValueKind.DWord, entry.Kind);
    }

    [Fact]
    public async Task Capture_PresentValue_RecordsExactKindAndData()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.CurrentUser, Key, Value, 0);
        var optimization = new WindowsBackgroundAppsOptimization();

        ChangeSet original = await optimization.CaptureAsync(context);

        RegistrySnapshotEntry entry = Assert.Single(original.Entries);
        Assert.Equal("windows-background-apps", original.OptimizationId);
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
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(out _);
        var optimization = new WindowsBackgroundAppsOptimization();

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
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        var optimization = new WindowsBackgroundAppsOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);

        OperationOutcome outcome = await optimization.ApplyAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Single(outcome.Changes);
        Assert.Equal(1, SystemTweaksTestHarness.ReadDword(registry, RegistryHive.CurrentUser, Key, Value));
    }

    [Fact]
    public async Task Apply_WhenCancelled_WritesNothing()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        var optimization = new WindowsBackgroundAppsOptimization();
        ChangeSet original = new() { OptimizationId = "windows-background-apps" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => optimization.ApplyAsync(context, original, cts.Token));

        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public async Task Restore_RestoresOriginalZero()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.CurrentUser, Key, Value, 0);
        var optimization = new WindowsBackgroundAppsOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);
        await optimization.ApplyAsync(context, original);

        OperationOutcome outcome = await optimization.RestoreAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Equal(0, SystemTweaksTestHarness.ReadDword(registry, RegistryHive.CurrentUser, Key, Value));
    }

    [Fact]
    public async Task Restore_DeletesValueThatWasOriginallyAbsent()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        var optimization = new WindowsBackgroundAppsOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);
        await optimization.ApplyAsync(context, original);
        Assert.NotNull(registry.ReadValue(RegistryHive.CurrentUser, Key, Value));

        OperationOutcome outcome = await optimization.RestoreAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Null(registry.ReadValue(RegistryHive.CurrentUser, Key, Value));
        Assert.Equal(1, registry.DeleteCount);
    }
}
