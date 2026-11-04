using ValorantOptimizer.Core;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests;

public sealed class UsbSelectiveSuspendTests
{
    private static OptimizationFixture Fixture() => new();

    [Fact]
    public void Descriptor_HasExpectedIdentityAndSafetyMetadata()
    {
        OptimizationDescriptor descriptor = new UsbSelectiveSuspendOptimization().Descriptor;

        Assert.Equal("usb-selective-suspend", descriptor.Id);
        Assert.Equal(OptimizationCategory.Usb, descriptor.Category);
        Assert.Equal(RiskLevel.Advanced, descriptor.Risk);
        Assert.True(descriptor.RequiresElevation);
        Assert.Equal(RestartRequirement.None, descriptor.Restart);
        Assert.Empty(descriptor.ManagedScopes);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Summary));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.WhatItChanges));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Why));
        Assert.NotEmpty(descriptor.Notes);
    }

    [Fact]
    public async Task Assess_WhenSelectiveSuspendIsDisabled_IsApplied()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(PowerSchemeTestData.EnglishQuery(
            PowerSchemeTestData.UsbSubgroupGuid,
            PowerSchemeTestData.SelectiveSuspendSettingGuid,
            acValue: 0));
        var optimization = new UsbSelectiveSuspendOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
        Assert.Contains(assessment.Evidence, item => item.Value.Contains("Disabled", StringComparison.Ordinal));
        Assert.Equal(0, fixture.Registry.WriteCount);
    }

    [Fact]
    public async Task Assess_WhenSelectiveSuspendIsEnabled_IsNotApplied()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(PowerSchemeTestData.GermanQuery(
            PowerSchemeTestData.UsbSubgroupGuid,
            PowerSchemeTestData.SelectiveSuspendSettingGuid,
            acValue: 1));
        var optimization = new UsbSelectiveSuspendOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains(assessment.Evidence, item => item.Value.Contains("Enabled", StringComparison.Ordinal));
        Assert.Contains(assessment.Evidence, item => item.Value.Contains("Ausbalanciert", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Assess_WhenPowerCfgQueryFails_IsUnsupportedAndNeverGuesses()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(string.Empty, querySucceeds: false);
        var optimization = new UsbSelectiveSuspendOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Unsupported, assessment.State);
        Assert.False(string.IsNullOrWhiteSpace(assessment.BlockedReason));
    }

    [Fact]
    public async Task Capture_RecordsActiveSchemeAndOriginalEnabledValue()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(PowerSchemeTestData.EnglishQuery(
            PowerSchemeTestData.UsbSubgroupGuid,
            PowerSchemeTestData.SelectiveSuspendSettingGuid,
            acValue: 1));
        var optimization = new UsbSelectiveSuspendOptimization();

        ChangeSet changeSet = await optimization.CaptureAsync(fixture.Context);

        Assert.Equal("usb-selective-suspend", changeSet.OptimizationId);
        Assert.Empty(changeSet.Entries);
        Assert.Equal(
            PowerSchemeTestData.BalancedSchemeGuid,
            changeSet.Metadata[PowerSchemeValueAccess.ActiveSchemeGuidMetadataKey]);
        Assert.Equal(
            "1",
            changeSet.Metadata[PowerSchemeValueAccess.OriginalAcValueMetadataKey]);
        Assert.Equal(0, fixture.Registry.WriteCount);
    }

    [Fact]
    public async Task Apply_DisablesSelectiveSuspendAndVerifies()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(PowerSchemeTestData.EnglishQuery(
            PowerSchemeTestData.UsbSubgroupGuid,
            PowerSchemeTestData.SelectiveSuspendSettingGuid,
            acValue: 0));
        var optimization = new UsbSelectiveSuspendOptimization();

        OperationOutcome outcome = await optimization.ApplyAsync(
            fixture.Context,
            PowerSchemeTestData.ChangeSetWithoutMetadata(optimization.Descriptor.Id));

        Assert.True(outcome.Success);
        Assert.True(outcome.Verified);
        Assert.Contains("no reboot", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, fixture.Processes.Invocations.Count);
        Assert.Equal(
            [
                "/setacvalueindex",
                "SCHEME_CURRENT",
                PowerSchemeTestData.UsbSubgroupGuid,
                PowerSchemeTestData.SelectiveSuspendSettingGuid,
                "0",
            ],
            fixture.Processes.Invocations[0].Arguments);
        Assert.Equal(["/setactive", "SCHEME_CURRENT"], fixture.Processes.Invocations[1].Arguments);
        Assert.Equal(0, fixture.Registry.WriteCount);
    }

    [Fact]
    public async Task Apply_WhenSetAcValueIndexFails_ReturnsFailure()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(
            string.Empty,
            setAcValueSucceeds: false);
        var optimization = new UsbSelectiveSuspendOptimization();

        OperationOutcome outcome = await optimization.ApplyAsync(
            fixture.Context,
            PowerSchemeTestData.ChangeSetWithoutMetadata(optimization.Descriptor.Id));

        Assert.False(outcome.Success);
        Assert.Single(fixture.Processes.Invocations);
        Assert.Contains("setacvalueindex", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_WhenVerificationShowsEnabled_ReturnsFailure()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(PowerSchemeTestData.EnglishQuery(
            PowerSchemeTestData.UsbSubgroupGuid,
            PowerSchemeTestData.SelectiveSuspendSettingGuid,
            acValue: 1));
        var optimization = new UsbSelectiveSuspendOptimization();

        OperationOutcome outcome = await optimization.ApplyAsync(
            fixture.Context,
            PowerSchemeTestData.ChangeSetWithoutMetadata(optimization.Descriptor.Id));

        Assert.False(outcome.Success);
        Assert.Contains("did not change", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restore_RestoresOriginalEnabledValue()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(PowerSchemeTestData.EnglishQuery(
            PowerSchemeTestData.UsbSubgroupGuid,
            PowerSchemeTestData.SelectiveSuspendSettingGuid,
            acValue: 1));
        var optimization = new UsbSelectiveSuspendOptimization();
        ChangeSet original = PowerSchemeTestData.PowerSettingChangeSet(
            optimization.Descriptor.Id,
            PowerSchemeTestData.BalancedSchemeGuid,
            originalAcValue: 1);

        OperationOutcome outcome = await optimization.RestoreAsync(fixture.Context, original);

        Assert.True(outcome.Success);
        Assert.True(outcome.Verified);
        Assert.Equal(3, fixture.Processes.Invocations.Count);
        Assert.Equal(
            [
                "/setacvalueindex",
                PowerSchemeTestData.BalancedSchemeGuid,
                PowerSchemeTestData.UsbSubgroupGuid,
                PowerSchemeTestData.SelectiveSuspendSettingGuid,
                "1",
            ],
            fixture.Processes.Invocations[0].Arguments);
        Assert.Equal(
            ["/setactive", PowerSchemeTestData.BalancedSchemeGuid],
            fixture.Processes.Invocations[1].Arguments);
    }

    [Fact]
    public async Task Restore_WhenMetadataIsMissing_ReturnsFailureWithoutInvokingPowerCfg()
    {
        var fixture = Fixture();
        var optimization = new UsbSelectiveSuspendOptimization();

        OperationOutcome outcome = await optimization.RestoreAsync(
            fixture.Context,
            PowerSchemeTestData.ChangeSetWithoutMetadata(optimization.Descriptor.Id));

        Assert.False(outcome.Success);
        Assert.Contains("backup", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Processes.Invocations);
    }

    [Fact]
    public async Task Restore_WhenVerifiedValueDiffers_ReturnsFailure()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(PowerSchemeTestData.EnglishQuery(
            PowerSchemeTestData.UsbSubgroupGuid,
            PowerSchemeTestData.SelectiveSuspendSettingGuid,
            acValue: 0));
        var optimization = new UsbSelectiveSuspendOptimization();

        OperationOutcome outcome = await optimization.RestoreAsync(
            fixture.Context,
            PowerSchemeTestData.PowerSettingChangeSet(
                optimization.Descriptor.Id,
                PowerSchemeTestData.BalancedSchemeGuid,
                originalAcValue: 1));

        Assert.False(outcome.Success);
        Assert.Contains("not restored", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllMethods_HonorPreCancelledToken()
    {
        var fixture = Fixture();
        var optimization = new UsbSelectiveSuspendOptimization();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        CancellationToken token = cancellation.Token;
        ChangeSet original = PowerSchemeTestData.PowerSettingChangeSet(
            optimization.Descriptor.Id,
            PowerSchemeTestData.BalancedSchemeGuid,
            originalAcValue: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => optimization.AssessAsync(fixture.Context, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => optimization.CaptureAsync(fixture.Context, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => optimization.ApplyAsync(fixture.Context, original, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => optimization.RestoreAsync(fixture.Context, original, token));
        Assert.Empty(fixture.Processes.Invocations);
    }
}
