using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Optimizations;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests.SystemTweaks;

public sealed class HagsTests
{
    private const string Key = HagsOptimization.RegistrySubKey;
    private const string Value = HagsOptimization.ValueName;

    [Fact]
    public void Descriptor_IsStableAndFullyDescribed()
    {
        var optimization = new HagsOptimization();

        SystemTweaksTestHarness.AssertWellFormedDescriptor(
            optimization,
            "gpu-hags",
            OptimizationCategory.Gpu,
            RiskLevel.Advanced,
            expectedRequiresElevation: true,
            RestartRequirement.Reboot,
            new RegistryScope(RegistryHive.LocalMachine, Key));

        Assert.Equal("HAGS on", optimization.Descriptor.StateWhenApplied);
        Assert.DoesNotContain("fps", optimization.Descriptor.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fps", optimization.Descriptor.Why, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("19041", optimization.Descriptor.Compatibility!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            optimization.Descriptor.Notes,
            note => note.Contains("reboot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Assess_ModernBuildWithHagsTwo_ReturnsApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        SystemTweaksTestHarness.SeedBuildNumber(registry, "22631");
        registry.SetDword(RegistryHive.LocalMachine, Key, Value, 2);
        var optimization = new HagsOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
        Assert.Equal("HAGS on", assessment.Headline);
        Assert.Contains(assessment.Evidence, evidence => evidence.Label == "Windows build" && evidence.Value == "22631");
        Assert.Contains(assessment.Evidence, evidence => evidence.Label == Value);
    }

    [Fact]
    public async Task Assess_Build19041Boundary_IsSupported()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        SystemTweaksTestHarness.SeedBuildNumber(registry, "19041");
        registry.SetDword(RegistryHive.LocalMachine, Key, Value, 2);
        var optimization = new HagsOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
    }

    [Fact]
    public async Task Assess_ModernBuildWithHagsOne_ReturnsNotApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        SystemTweaksTestHarness.SeedBuildNumber(registry, "22631");
        registry.SetDword(RegistryHive.LocalMachine, Key, Value, 1);
        var optimization = new HagsOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Equal("HAGS off", assessment.Headline);
        Assert.False(string.IsNullOrWhiteSpace(assessment.Explanation));
    }

    [Fact]
    public async Task Assess_ModernBuildWithHagsMissing_ReturnsDefaultNotApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        SystemTweaksTestHarness.SeedBuildNumber(registry, "22631");
        var optimization = new HagsOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains("default", assessment.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == Value &&
                        evidence.Value.Contains("default", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Assess_BuildBefore19041_ReturnsUnsupported()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        SystemTweaksTestHarness.SeedBuildNumber(registry, "19040");
        registry.SetDword(RegistryHive.LocalMachine, Key, Value, 2);
        var optimization = new HagsOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.Unsupported, assessment.State);
        Assert.Contains("19041", assessment.BlockedReason);
        Assert.False(assessment.IsActionable);
    }

    [Fact]
    public async Task Assess_MissingBuildNumber_ReturnsUnsupported()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        var optimization = new HagsOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.Unsupported, assessment.State);
        Assert.Contains("could not be determined", assessment.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Assess_UnparsableBuildNumber_ReturnsUnsupported()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        SystemTweaksTestHarness.SeedBuildNumber(registry, "not-a-build");
        var optimization = new HagsOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.Unsupported, assessment.State);
        Assert.Contains("not-a-build", Assert.Single(assessment.Evidence).Value);
    }

    [Fact]
    public async Task Assess_DoesNotWrite()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        SystemTweaksTestHarness.SeedBuildNumber(registry, "22631");
        registry.SetDword(RegistryHive.LocalMachine, Key, Value, 1);
        int writesAfterSeeding = registry.WriteCount;
        var optimization = new HagsOptimization();

        await optimization.AssessAsync(context);

        Assert.Equal(writesAfterSeeding, registry.WriteCount);
        Assert.Equal(0, registry.DeleteCount);
    }

    [Fact]
    public async Task Capture_RecordsExactOriginalAndMissingState()
    {
        FakeRegistryStore presentRegistry = new();
        OptimizationContext presentContext = SystemTweaksTestHarness.CreateContext(presentRegistry);
        presentRegistry.SetDword(RegistryHive.LocalMachine, Key, Value, 1);
        var optimization = new HagsOptimization();

        ChangeSet present = await optimization.CaptureAsync(presentContext);
        RegistrySnapshotEntry presentEntry = Assert.Single(present.Entries);
        Assert.True(presentEntry.Existed);
        Assert.Equal(RegistryValueKind.DWord, presentEntry.Kind);
        Assert.Equal("1", presentEntry.SerializedData);

        OptimizationContext missingContext = SystemTweaksTestHarness.CreateContext(out _);
        ChangeSet missing = await optimization.CaptureAsync(missingContext);
        Assert.False(Assert.Single(missing.Entries).Existed);
    }

    [Fact]
    public async Task Apply_WritesDwordTwoOnSupportedBuild()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry, isElevated: true);
        SystemTweaksTestHarness.SeedBuildNumber(registry, "22631");
        var optimization = new HagsOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);

        OperationOutcome outcome = await optimization.ApplyAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Single(outcome.Changes);
        Assert.Equal(2, SystemTweaksTestHarness.ReadDword(registry, RegistryHive.LocalMachine, Key, Value));
    }

    [Fact]
    public async Task Apply_OnUnsupportedBuild_FailsWithoutWriting()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry, isElevated: true);
        SystemTweaksTestHarness.SeedBuildNumber(registry, "18363");
        int writesAfterSeeding = registry.WriteCount;
        var optimization = new HagsOptimization();
        ChangeSet original = new() { OptimizationId = "gpu-hags" };

        OperationOutcome outcome = await optimization.ApplyAsync(context, original);

        Assert.False(outcome.Success);
        Assert.Contains("19041", outcome.Message);
        Assert.Equal(writesAfterSeeding, registry.WriteCount);
        Assert.Null(registry.ReadValue(RegistryHive.LocalMachine, Key, Value));
    }

    [Fact]
    public async Task Apply_WhenCancelled_WritesNothing()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry);
        SystemTweaksTestHarness.SeedBuildNumber(registry, "22631");
        var optimization = new HagsOptimization();
        ChangeSet original = new() { OptimizationId = "gpu-hags" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => optimization.ApplyAsync(context, original, cts.Token));

        Assert.Null(registry.ReadValue(RegistryHive.LocalMachine, Key, Value));
    }

    [Fact]
    public async Task Restore_RestoresOriginalDisabledMode()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry, isElevated: true);
        SystemTweaksTestHarness.SeedBuildNumber(registry, "22631");
        registry.SetDword(RegistryHive.LocalMachine, Key, Value, 1);
        var optimization = new HagsOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);
        await optimization.ApplyAsync(context, original);

        OperationOutcome outcome = await optimization.RestoreAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Equal(1, SystemTweaksTestHarness.ReadDword(registry, RegistryHive.LocalMachine, Key, Value));
    }

    [Fact]
    public async Task Restore_DeletesValueThatWasOriginallyAbsent()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = SystemTweaksTestHarness.CreateContext(registry, isElevated: true);
        SystemTweaksTestHarness.SeedBuildNumber(registry, "22631");
        var optimization = new HagsOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);
        await optimization.ApplyAsync(context, original);

        OperationOutcome outcome = await optimization.RestoreAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Null(registry.ReadValue(RegistryHive.LocalMachine, Key, Value));
        Assert.Equal(1, registry.DeleteCount);
    }
}
