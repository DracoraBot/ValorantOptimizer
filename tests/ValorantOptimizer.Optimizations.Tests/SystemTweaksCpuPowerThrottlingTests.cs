using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Optimizations;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests.SystemTweaks;

public sealed class CpuPowerThrottlingTests
{
    private const string Key = CpuPowerThrottlingOptimization.RegistrySubKey;
    private const string Value = CpuPowerThrottlingOptimization.ValueName;

    [Fact]
    public void Descriptor_IsStableAndFullyDescribed()
    {
        var optimization = new CpuPowerThrottlingOptimization();

        SystemTweaksTestHarness.AssertWellFormedDescriptor(
            optimization,
            "cpu-power-throttling",
            OptimizationCategory.CpuPower,
            RiskLevel.Advanced,
            expectedRequiresElevation: true,
            RestartRequirement.Reboot,
            new RegistryScope(RegistryHive.LocalMachine, Key));

        Assert.Equal("Power throttling off", optimization.Descriptor.StateWhenApplied);
        Assert.DoesNotContain("fps", optimization.Descriptor.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fps", optimization.Descriptor.Why, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            optimization.Descriptor.Notes,
            note => note.Contains("battery", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            optimization.Descriptor.Notes,
            note => note.Contains("reboot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Assess_DwordOne_ReturnsApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.LocalMachine, Key, Value, 1);
        var optimization = new CpuPowerThrottlingOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
        Assert.Equal("Power throttling off", assessment.Headline);
        Assert.Contains("off", Assert.Single(assessment.Evidence).Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Assess_DwordZero_ReturnsNotApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.LocalMachine, Key, Value, 0);
        var optimization = new CpuPowerThrottlingOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Equal("Power throttling on", assessment.Headline);
        Assert.False(string.IsNullOrWhiteSpace(assessment.Explanation));
    }

    [Fact]
    public async Task Assess_MissingValue_ReturnsDefaultNotApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        var optimization = new CpuPowerThrottlingOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains("default", assessment.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public async Task Assess_UnexpectedValueKind_ReturnsNotApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        registry.SetString(RegistryHive.LocalMachine, Key, Value, "1");
        var optimization = new CpuPowerThrottlingOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains("String", Assert.Single(assessment.Evidence).Value);
        Assert.Contains("DWORD", assessment.Explanation);
    }

    [Fact]
    public async Task AssessAndCapture_DoNotWrite()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry, isElevated: true);
        registry.SetDword(RegistryHive.LocalMachine, Key, Value, 0);
        int writesAfterSeeding = registry.WriteCount;
        var optimization = new CpuPowerThrottlingOptimization();

        await optimization.AssessAsync(context);
        ChangeSet original = await optimization.CaptureAsync(context);

        Assert.Equal(writesAfterSeeding, registry.WriteCount);
        Assert.Equal(0, registry.DeleteCount);
        Assert.True(Assert.Single(original.Entries).Existed);
    }

    [Fact]
    public async Task Capture_RecordsExactOriginalAndMissingState()
    {
        FakeRegistryStore presentRegistry = new();
        OptimizationContext presentContext = SystemTweaksTestHarness.CreateContext(presentRegistry);
        presentRegistry.SetDword(RegistryHive.LocalMachine, Key, Value, 0);
        var optimization = new CpuPowerThrottlingOptimization();

        ChangeSet present = await optimization.CaptureAsync(presentContext);
        RegistrySnapshotEntry presentEntry = Assert.Single(present.Entries);
        Assert.Equal(RegistryHive.LocalMachine, presentEntry.Hive);
        Assert.True(presentEntry.Existed);
        Assert.Equal(RegistryValueKind.DWord, presentEntry.Kind);
        Assert.Equal("0", presentEntry.SerializedData);

        OptimizationContext missingContext = SystemTweaksTestHarness.CreateContext(out _);
        ChangeSet missing = await optimization.CaptureAsync(missingContext);
        RegistrySnapshotEntry missingEntry = Assert.Single(missing.Entries);
        Assert.False(missingEntry.Existed);
        Assert.Null(missingEntry.SerializedData);
    }

    [Fact]
    public async Task Apply_WritesDwordOne()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry, isElevated: true);
        var optimization = new CpuPowerThrottlingOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);

        OperationOutcome outcome = await optimization.ApplyAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Single(outcome.Changes);
        Assert.Equal(1, SystemTweaksTestHarness.ReadDword(registry, RegistryHive.LocalMachine, Key, Value));
    }

    [Fact]
    public async Task Apply_WhenCancelled_WritesNothing()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        var optimization = new CpuPowerThrottlingOptimization();
        ChangeSet original = new() { OptimizationId = "cpu-power-throttling" };
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
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry, isElevated: true);
        registry.SetDword(RegistryHive.LocalMachine, Key, Value, 0);
        var optimization = new CpuPowerThrottlingOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);
        await optimization.ApplyAsync(context, original);

        OperationOutcome outcome = await optimization.RestoreAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Equal(0, SystemTweaksTestHarness.ReadDword(registry, RegistryHive.LocalMachine, Key, Value));
    }

    [Fact]
    public async Task Restore_DeletesValueThatWasOriginallyAbsent()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry, isElevated: true);
        var optimization = new CpuPowerThrottlingOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);
        await optimization.ApplyAsync(context, original);

        OperationOutcome outcome = await optimization.RestoreAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Null(registry.ReadValue(RegistryHive.LocalMachine, Key, Value));
        Assert.Equal(1, registry.DeleteCount);
    }
}
