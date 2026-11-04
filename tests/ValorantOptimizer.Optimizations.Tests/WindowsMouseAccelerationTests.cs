using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Optimizations;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests.Windows;

public sealed class MouseAccelerationOptimizationTests
{
    private static readonly string[] ValueNames =
    [
        MouseAccelerationOptimization.MouseSpeedValueName,
        MouseAccelerationOptimization.MouseThreshold1ValueName,
        MouseAccelerationOptimization.MouseThreshold2ValueName,
    ];

    [Fact]
    public void Descriptor_IsStableAndFullyDescribed()
    {
        var optimization = new MouseAccelerationOptimization(new FakeMouseAccelerationApplier());

        WindowsTestHarness.AssertWellFormedDescriptor(
            optimization,
            "mouse-acceleration",
            OptimizationCategory.Mouse,
            RiskLevel.Safe,
            expectedRequiresElevation: false,
            new RegistryScope(RegistryHive.CurrentUser, @"Control Panel\Mouse"));

        Assert.Equal("Acceleration off", optimization.Descriptor.StateWhenApplied);
        Assert.Equal("Disable mouse acceleration", optimization.Descriptor.Name);
        Assert.DoesNotContain("fps", optimization.Descriptor.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fps", optimization.Descriptor.Why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Assess_AllThreeValuesZero_ReturnsApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        foreach (string valueName in ValueNames)
        {
            registry.SetString(RegistryHive.CurrentUser, MouseAccelerationOptimization.RegistrySubKey, valueName, "0");
        }

        var optimization = new MouseAccelerationOptimization(new FakeMouseAccelerationApplier());
        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
        Assert.Equal("Acceleration off", assessment.Headline);
        Assert.Equal(3, assessment.Evidence.Count);
        Assert.All(assessment.Evidence, evidence => Assert.Equal("0", evidence.Value));
        Assert.True(assessment.IsActionable);
    }

    [Fact]
    public async Task Assess_MixedValues_ReturnsNotAppliedWithEvidence()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetString(RegistryHive.CurrentUser, MouseAccelerationOptimization.RegistrySubKey, "MouseSpeed", "1");
        registry.SetString(RegistryHive.CurrentUser, MouseAccelerationOptimization.RegistrySubKey, "MouseThreshold1", "6");
        registry.SetString(RegistryHive.CurrentUser, MouseAccelerationOptimization.RegistrySubKey, "MouseThreshold2", "10");

        var optimization = new MouseAccelerationOptimization(new FakeMouseAccelerationApplier());
        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Equal("Acceleration on", assessment.Headline);
        Assert.False(string.IsNullOrWhiteSpace(assessment.Explanation));
        Assert.Equal(3, assessment.Evidence.Count);
        Assert.Contains(assessment.Evidence, evidence => evidence.Value == "1");
        Assert.Contains(assessment.Evidence, evidence => evidence.Value == "10");
    }

    [Fact]
    public async Task Assess_AllValuesMissing_ReturnsDefaultNotApplied()
    {
        OptimizationContext context = WindowsTestHarness.CreateContext(out _);

        var optimization = new MouseAccelerationOptimization(new FakeMouseAccelerationApplier());
        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains("default", assessment.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.All(
            assessment.Evidence,
            evidence => Assert.Contains("default", evidence.Value, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AssessAndCapture_NeverWriteOrInvokeApplier()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        foreach (string valueName in ValueNames)
        {
            registry.SetString(RegistryHive.CurrentUser, MouseAccelerationOptimization.RegistrySubKey, valueName, "1");
        }

        int writesAfterSeeding = registry.WriteCount;
        var applier = new FakeMouseAccelerationApplier();
        var optimization = new MouseAccelerationOptimization(applier);

        await optimization.AssessAsync(context);
        await optimization.CaptureAsync(context);

        Assert.Equal(0, applier.CallCount);
        Assert.Equal(writesAfterSeeding, registry.WriteCount);
        Assert.Equal(0, registry.DeleteCount);
    }

    [Fact]
    public async Task Capture_RecordsEveryValueWithExactOriginalKindAndData()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetString(RegistryHive.CurrentUser, MouseAccelerationOptimization.RegistrySubKey, "MouseSpeed", "1");
        registry.SetString(RegistryHive.CurrentUser, MouseAccelerationOptimization.RegistrySubKey, "MouseThreshold1", "6");
        registry.SetString(RegistryHive.CurrentUser, MouseAccelerationOptimization.RegistrySubKey, "MouseThreshold2", "10");

        var optimization = new MouseAccelerationOptimization(new FakeMouseAccelerationApplier());
        ChangeSet original = await optimization.CaptureAsync(context);

        Assert.Equal("mouse-acceleration", original.OptimizationId);
        Assert.Equal(3, original.Entries.Count);
        Assert.Equal(
            ValueNames,
            original.Entries.Select(entry => entry.ValueName).ToArray());

        foreach (RegistrySnapshotEntry entry in original.Entries)
        {
            Assert.Equal(RegistryHive.CurrentUser, entry.Hive);
            Assert.Equal(MouseAccelerationOptimization.RegistrySubKey, entry.SubKey);
            Assert.True(entry.Existed);
            Assert.Equal(RegistryValueKind.String, entry.Kind);
        }

        Assert.Equal("1", original.Entries[0].SerializedData);
        Assert.Equal("6", original.Entries[1].SerializedData);
        Assert.Equal("10", original.Entries[2].SerializedData);
    }

    [Fact]
    public async Task Capture_MissingValues_AreRecordedAsNotExisted()
    {
        OptimizationContext context = WindowsTestHarness.CreateContext(out _);

        var optimization = new MouseAccelerationOptimization(new FakeMouseAccelerationApplier());
        ChangeSet original = await optimization.CaptureAsync(context);

        Assert.Equal(3, original.Entries.Count);
        Assert.All(original.Entries, entry => Assert.False(entry.Existed));
        Assert.All(original.Entries, entry => Assert.Null(entry.SerializedData));
    }

    [Fact]
    public async Task Apply_WritesZeroStringsAndInvokesApplierOnce()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        foreach (string valueName in ValueNames)
        {
            registry.SetString(RegistryHive.CurrentUser, MouseAccelerationOptimization.RegistrySubKey, valueName, "1");
        }

        int writesAfterSeeding = registry.WriteCount;
        var applier = new FakeMouseAccelerationApplier();
        var optimization = new MouseAccelerationOptimization(applier);
        ChangeSet original = await optimization.CaptureAsync(context);

        OperationOutcome outcome = await optimization.ApplyAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Equal(1, applier.CallCount);
        Assert.Equal(0, applier.LastSpeed);
        Assert.Equal(0, applier.LastThreshold1);
        Assert.Equal(0, applier.LastThreshold2);
        Assert.Equal(writesAfterSeeding + 3, registry.WriteCount);
        Assert.Equal(3, outcome.Changes.Count);
        Assert.Empty(outcome.Warnings);

        foreach (string valueName in ValueNames)
        {
            RegistryValue? value = registry.ReadValue(
                RegistryHive.CurrentUser,
                MouseAccelerationOptimization.RegistrySubKey,
                valueName);
            Assert.NotNull(value);
            Assert.Equal(RegistryValueKind.String, value.Kind);
            Assert.Equal("0", Assert.IsType<string>(value.Data));
        }
    }

    [Fact]
    public async Task Apply_ApplierFailure_IsSuccessfulWithSignOutWarning()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        var applier = new FakeMouseAccelerationApplier { Succeeds = false };
        var optimization = new MouseAccelerationOptimization(applier);
        ChangeSet original = await optimization.CaptureAsync(context);

        OperationOutcome outcome = await optimization.ApplyAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Equal(1, applier.CallCount);
        string warning = Assert.Single(outcome.Warnings);
        Assert.Contains("sign out", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_WhenCancelled_WritesNothingAndDoesNotInvokeApplier()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        int writesAfterSeeding = registry.WriteCount;
        var applier = new FakeMouseAccelerationApplier();
        var optimization = new MouseAccelerationOptimization(applier);
        ChangeSet original = new() { OptimizationId = "mouse-acceleration" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => optimization.ApplyAsync(context, original, cts.Token));

        Assert.Equal(0, applier.CallCount);
        Assert.Equal(writesAfterSeeding, registry.WriteCount);
    }

    [Fact]
    public async Task Apply_IsIdempotent()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        var applier = new FakeMouseAccelerationApplier();
        var optimization = new MouseAccelerationOptimization(applier);
        ChangeSet original = await optimization.CaptureAsync(context);

        OperationOutcome first = await optimization.ApplyAsync(context, original);
        OperationOutcome second = await optimization.ApplyAsync(context, original);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(2, applier.CallCount);
        foreach (string valueName in ValueNames)
        {
            RegistryValue? value = registry.ReadValue(
                RegistryHive.CurrentUser,
                MouseAccelerationOptimization.RegistrySubKey,
                valueName);
            Assert.NotNull(value);
            Assert.Equal("0", Assert.IsType<string>(value.Data));
        }
    }

    [Fact]
    public async Task Restore_ReproducesOriginalsAndDeletesValuesThatWereAbsent()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetString(RegistryHive.CurrentUser, MouseAccelerationOptimization.RegistrySubKey, "MouseSpeed", "1");
        // MouseThreshold1 is deliberately absent -> must be deleted again by undo.
        registry.SetString(RegistryHive.CurrentUser, MouseAccelerationOptimization.RegistrySubKey, "MouseThreshold2", "8");

        var optimization = new MouseAccelerationOptimization(new FakeMouseAccelerationApplier());
        ChangeSet original = await optimization.CaptureAsync(context);
        await optimization.ApplyAsync(context, original);

        OperationOutcome outcome = await optimization.RestoreAsync(context, original);

        Assert.True(outcome.Success);
        RegistryValue? speed = registry.ReadValue(
            RegistryHive.CurrentUser, MouseAccelerationOptimization.RegistrySubKey, "MouseSpeed");
        Assert.NotNull(speed);
        Assert.Equal(RegistryValueKind.String, speed.Kind);
        Assert.Equal("1", Assert.IsType<string>(speed.Data));

        Assert.Null(registry.ReadValue(
            RegistryHive.CurrentUser, MouseAccelerationOptimization.RegistrySubKey, "MouseThreshold1"));

        RegistryValue? threshold2 = registry.ReadValue(
            RegistryHive.CurrentUser, MouseAccelerationOptimization.RegistrySubKey, "MouseThreshold2");
        Assert.NotNull(threshold2);
        Assert.Equal("8", Assert.IsType<string>(threshold2.Data));
        Assert.Equal(1, registry.DeleteCount);
    }

    [Fact]
    public async Task Restore_RefreshesLiveSessionWithCapturedOriginalValues()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetString(RegistryHive.CurrentUser, MouseAccelerationOptimization.RegistrySubKey, "MouseSpeed", "1");
        registry.SetString(RegistryHive.CurrentUser, MouseAccelerationOptimization.RegistrySubKey, "MouseThreshold1", "6");
        registry.SetString(RegistryHive.CurrentUser, MouseAccelerationOptimization.RegistrySubKey, "MouseThreshold2", "10");

        var applier = new FakeMouseAccelerationApplier();
        var optimization = new MouseAccelerationOptimization(applier);
        ChangeSet original = await optimization.CaptureAsync(context);
        await optimization.ApplyAsync(context, original);

        OperationOutcome outcome = await optimization.RestoreAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Equal(2, applier.CallCount);
        Assert.Equal(1, applier.LastSpeed);
        Assert.Equal(6, applier.LastThreshold1);
        Assert.Equal(10, applier.LastThreshold2);
        Assert.Empty(outcome.Warnings);
    }

    [Fact]
    public async Task Restore_LiveRefreshFailure_ReportsSignOutWarning()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetString(RegistryHive.CurrentUser, MouseAccelerationOptimization.RegistrySubKey, "MouseSpeed", "1");

        var applier = new FakeMouseAccelerationApplier { Succeeds = false };
        var optimization = new MouseAccelerationOptimization(applier);
        ChangeSet original = await optimization.CaptureAsync(context);

        OperationOutcome outcome = await optimization.RestoreAsync(context, original);

        Assert.True(outcome.Success);
        string warning = Assert.Single(outcome.Warnings);
        Assert.Contains("Sign out", warning, StringComparison.OrdinalIgnoreCase);
    }
}
