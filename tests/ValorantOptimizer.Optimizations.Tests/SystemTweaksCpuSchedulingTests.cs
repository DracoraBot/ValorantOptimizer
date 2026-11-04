using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Optimizations;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests.SystemTweaks;

public sealed class CpuSchedulingTests
{
    private const string Key = CpuSchedulingOptimization.RegistrySubKey;
    private const string Value = CpuSchedulingOptimization.ValueName;

    [Fact]
    public void Descriptor_IsStableAndFullyDescribed()
    {
        var optimization = new CpuSchedulingOptimization();

        SystemTweaksTestHarness.AssertWellFormedDescriptor(
            optimization,
            "cpu-scheduling",
            OptimizationCategory.CpuPower,
            RiskLevel.Experimental,
            expectedRequiresElevation: true,
            RestartRequirement.Reboot,
            new RegistryScope(RegistryHive.LocalMachine, Key));

        Assert.Equal("0x26 foreground quantum", optimization.Descriptor.StateWhenApplied);
        Assert.DoesNotContain("fps", optimization.Descriptor.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fps", optimization.Descriptor.Why, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            optimization.Descriptor.Notes,
            note => note.Contains("disputed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            optimization.Descriptor.Notes,
            note => note.Contains("Experimental", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Assess_RecommendedValue_ReturnsApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.LocalMachine, Key, Value, 38);
        var optimization = new CpuSchedulingOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
        Assert.Equal("0x26 foreground quantum", assessment.Headline);
        Assert.Contains("38", Assert.Single(assessment.Evidence).Value);
        Assert.Contains("0x26", Assert.Single(assessment.Evidence).Value);
    }

    [Fact]
    public async Task Assess_WindowsDefaultTwo_ReturnsNotApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.LocalMachine, Key, Value, 2);
        var optimization = new CpuSchedulingOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains("default", assessment.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(assessment.Explanation));
    }

    [Fact]
    public async Task Assess_MissingValue_ReturnsDefaultNotApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        var optimization = new CpuSchedulingOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains("default", assessment.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public async Task Assess_CustomValue_ReturnsNotAppliedWithCurrentValue()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.LocalMachine, Key, Value, 24);
        var optimization = new CpuSchedulingOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains("24", assessment.Headline);
        Assert.Contains("0x18", assessment.Headline);
    }

    [Fact]
    public async Task Assess_UnexpectedValueKind_ReturnsNotApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        registry.SetString(RegistryHive.LocalMachine, Key, Value, "38");
        var optimization = new CpuSchedulingOptimization();

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
        registry.SetDword(RegistryHive.LocalMachine, Key, Value, 2);
        int writesAfterSeeding = registry.WriteCount;
        var optimization = new CpuSchedulingOptimization();

        await optimization.AssessAsync(context);
        ChangeSet original = await optimization.CaptureAsync(context);

        Assert.Equal(writesAfterSeeding, registry.WriteCount);
        Assert.Equal(0, registry.DeleteCount);
        RegistrySnapshotEntry entry = Assert.Single(original.Entries);
        Assert.True(entry.Existed);
        Assert.Equal("2", entry.SerializedData);
    }

    [Fact]
    public async Task Capture_RecordsExactOriginalAndMissingState()
    {
        FakeRegistryStore presentRegistry = new();
        OptimizationContext presentContext = SystemTweaksTestHarness.CreateContext(presentRegistry);
        presentRegistry.SetDword(RegistryHive.LocalMachine, Key, Value, 2);
        var optimization = new CpuSchedulingOptimization();

        ChangeSet present = await optimization.CaptureAsync(presentContext);
        Assert.Equal("2", Assert.Single(present.Entries).SerializedData);

        OptimizationContext missingContext = SystemTweaksTestHarness.CreateContext(out _);
        ChangeSet missing = await optimization.CaptureAsync(missingContext);
        Assert.False(Assert.Single(missing.Entries).Existed);
    }

    [Fact]
    public async Task Apply_WritesDword38()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry, isElevated: true);
        var optimization = new CpuSchedulingOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);

        OperationOutcome outcome = await optimization.ApplyAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Single(outcome.Changes);
        Assert.Equal(38, SystemTweaksTestHarness.ReadDword(registry, RegistryHive.LocalMachine, Key, Value));
    }

    [Fact]
    public async Task Apply_WhenCancelled_WritesNothing()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        var optimization = new CpuSchedulingOptimization();
        ChangeSet original = new() { OptimizationId = "cpu-scheduling" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => optimization.ApplyAsync(context, original, cts.Token));

        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public async Task Restore_RestoresOriginalValue()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry, isElevated: true);
        registry.SetDword(RegistryHive.LocalMachine, Key, Value, 2);
        var optimization = new CpuSchedulingOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);
        await optimization.ApplyAsync(context, original);

        OperationOutcome outcome = await optimization.RestoreAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Equal(2, SystemTweaksTestHarness.ReadDword(registry, RegistryHive.LocalMachine, Key, Value));
    }

    [Fact]
    public async Task Restore_DeletesValueThatWasOriginallyAbsent()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry, isElevated: true);
        var optimization = new CpuSchedulingOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);
        await optimization.ApplyAsync(context, original);

        OperationOutcome outcome = await optimization.RestoreAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Null(registry.ReadValue(RegistryHive.LocalMachine, Key, Value));
        Assert.Equal(1, registry.DeleteCount);
    }
}
