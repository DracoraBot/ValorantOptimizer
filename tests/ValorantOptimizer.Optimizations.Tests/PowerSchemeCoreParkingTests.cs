using ValorantOptimizer.Core;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests;

public sealed class PowerSchemeCoreParkingTests
{
    private static OptimizationFixture Fixture() => new();

    [Fact]
    public void Descriptor_HasExpectedIdentityAndSafetyMetadata()
    {
        OptimizationDescriptor descriptor = new CpuCoreParkingOptimization().Descriptor;

        Assert.Equal("cpu-core-parking", descriptor.Id);
        Assert.Equal(OptimizationCategory.CpuPower, descriptor.Category);
        Assert.Equal(RiskLevel.Advanced, descriptor.Risk);
        Assert.True(descriptor.RequiresElevation);
        Assert.Equal(RestartRequirement.None, descriptor.Restart);
        Assert.Empty(descriptor.ManagedScopes);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Summary));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.WhatItChanges));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Why));
        Assert.NotEmpty(descriptor.Notes);
        Assert.Contains(
            descriptor.Notes,
            note => note.Contains("DC/battery", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AllTask5Modules_PassEngineCatalogValidation()
    {
        Assert.Empty(OptimizationCatalogValidator.Validate(
        [
            new CpuCoreParkingOptimization(),
            new UsbSelectiveSuspendOptimization(),
            new NetworkAdapterPowerOptimization(),
        ]));
    }

    [Fact]
    public async Task Assess_WhenAcValueIsRecommended_IsApplied()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(PowerSchemeTestData.EnglishQuery(
            PowerSchemeTestData.ProcessorSubgroupGuid,
            PowerSchemeTestData.CoreParkingSettingGuid,
            acValue: 100));
        var optimization = new CpuCoreParkingOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
        Assert.Contains(assessment.Evidence, item => item.Value.Contains("Balanced", StringComparison.Ordinal));
        Assert.Contains(assessment.Evidence, item => item.Value.Contains("100%", StringComparison.Ordinal));
        Assert.Equal(0, fixture.Registry.WriteCount);
    }

    [Fact]
    public async Task Assess_WhenAcValueIsDifferent_IsNotApplied()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(PowerSchemeTestData.GermanQuery(
            PowerSchemeTestData.ProcessorSubgroupGuid,
            PowerSchemeTestData.CoreParkingSettingGuid,
            acValue: 25));
        var optimization = new CpuCoreParkingOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains(assessment.Evidence, item => item.Value.Contains("25%", StringComparison.Ordinal));
        Assert.Contains(assessment.Evidence, item => item.Value.Contains("Ausbalanciert", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Assess_WhenPowerCfgQueryFails_IsUnsupportedAndNeverGuesses()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(string.Empty, querySucceeds: false);
        var optimization = new CpuCoreParkingOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Unsupported, assessment.State);
        Assert.False(string.IsNullOrWhiteSpace(assessment.BlockedReason));
        Assert.Equal(0, fixture.Registry.WriteCount);
    }

    [Fact]
    public async Task Assess_WhenOutputCannotBeParsed_IsUnsupported()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler("not powercfg output");
        var optimization = new CpuCoreParkingOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Unsupported, assessment.State);
        Assert.Contains("unreadable", assessment.BlockedReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Capture_RecordsActiveSchemeAndOriginalAcValueOnly()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(PowerSchemeTestData.EnglishQuery(
            PowerSchemeTestData.ProcessorSubgroupGuid,
            PowerSchemeTestData.CoreParkingSettingGuid,
            acValue: 25));
        var optimization = new CpuCoreParkingOptimization();

        ChangeSet changeSet = await optimization.CaptureAsync(fixture.Context);

        Assert.Equal("cpu-core-parking", changeSet.OptimizationId);
        Assert.Empty(changeSet.Entries);
        Assert.Equal(
            PowerSchemeTestData.BalancedSchemeGuid,
            changeSet.Metadata[PowerSchemeValueAccess.ActiveSchemeGuidMetadataKey]);
        Assert.Equal(
            "25",
            changeSet.Metadata[PowerSchemeValueAccess.OriginalAcValueMetadataKey]);
        Assert.Equal(0, fixture.Registry.WriteCount);
        Assert.Single(fixture.Processes.Invocations);
        Assert.Equal(
            ["/query", "SCHEME_CURRENT", PowerSchemeTestData.ProcessorSubgroupGuid, PowerSchemeTestData.CoreParkingSettingGuid],
            fixture.Processes.Invocations[0].Arguments);
    }

    [Fact]
    public async Task Capture_WhenQueryFails_Throws()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(string.Empty, querySucceeds: false);
        var optimization = new CpuCoreParkingOptimization();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => optimization.CaptureAsync(fixture.Context));
    }

    [Fact]
    public async Task Apply_SetsRecommendedValueReactivatesAndVerifies()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(PowerSchemeTestData.EnglishQuery(
            PowerSchemeTestData.ProcessorSubgroupGuid,
            PowerSchemeTestData.CoreParkingSettingGuid,
            acValue: 100));
        var optimization = new CpuCoreParkingOptimization();
        ChangeSet original = PowerSchemeTestData.ChangeSetWithoutMetadata(optimization.Descriptor.Id);

        OperationOutcome outcome = await optimization.ApplyAsync(fixture.Context, original);

        Assert.True(outcome.Success);
        Assert.True(outcome.Verified);
        Assert.Contains("no reboot", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Registry.WriteCount);

        Assert.Equal(3, fixture.Processes.Invocations.Count);
        Assert.Equal(
            [
                "/setacvalueindex",
                "SCHEME_CURRENT",
                PowerSchemeTestData.ProcessorSubgroupGuid,
                PowerSchemeTestData.CoreParkingSettingGuid,
                "100",
            ],
            fixture.Processes.Invocations[0].Arguments);
        Assert.Equal(["/setactive", "SCHEME_CURRENT"], fixture.Processes.Invocations[1].Arguments);
        Assert.Equal(
            ["/query", "SCHEME_CURRENT", PowerSchemeTestData.ProcessorSubgroupGuid, PowerSchemeTestData.CoreParkingSettingGuid],
            fixture.Processes.Invocations[2].Arguments);
    }

    [Fact]
    public async Task Apply_WhenSetAcValueIndexFails_ReturnsFailure()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(
            string.Empty,
            setAcValueSucceeds: false);
        var optimization = new CpuCoreParkingOptimization();

        OperationOutcome outcome = await optimization.ApplyAsync(
            fixture.Context,
            PowerSchemeTestData.ChangeSetWithoutMetadata(optimization.Descriptor.Id));

        Assert.False(outcome.Success);
        Assert.Single(fixture.Processes.Invocations);
        Assert.Contains("setacvalueindex", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_WhenSetActiveFails_ReturnsFailure()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(
            string.Empty,
            setActiveSucceeds: false);
        var optimization = new CpuCoreParkingOptimization();

        OperationOutcome outcome = await optimization.ApplyAsync(
            fixture.Context,
            PowerSchemeTestData.ChangeSetWithoutMetadata(optimization.Descriptor.Id));

        Assert.False(outcome.Success);
        Assert.Equal(2, fixture.Processes.Invocations.Count);
        Assert.Contains("reactivated", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_WhenVerificationFails_ReturnsFailure()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(
            string.Empty,
            querySucceeds: false);
        var optimization = new CpuCoreParkingOptimization();

        OperationOutcome outcome = await optimization.ApplyAsync(
            fixture.Context,
            PowerSchemeTestData.ChangeSetWithoutMetadata(optimization.Descriptor.Id));

        Assert.False(outcome.Success);
        Assert.Equal(3, fixture.Processes.Invocations.Count);
        Assert.Contains("could not be verified", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_WhenVerifiedValueDiffers_ReturnsFailure()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(PowerSchemeTestData.EnglishQuery(
            PowerSchemeTestData.ProcessorSubgroupGuid,
            PowerSchemeTestData.CoreParkingSettingGuid,
            acValue: 50));
        var optimization = new CpuCoreParkingOptimization();

        OperationOutcome outcome = await optimization.ApplyAsync(
            fixture.Context,
            PowerSchemeTestData.ChangeSetWithoutMetadata(optimization.Descriptor.Id));

        Assert.False(outcome.Success);
        Assert.Contains("did not change", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Undo", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restore_WritesCapturedValueToCapturedSchemeAndVerifies()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(PowerSchemeTestData.EnglishQuery(
            PowerSchemeTestData.ProcessorSubgroupGuid,
            PowerSchemeTestData.CoreParkingSettingGuid,
            acValue: 25));
        var optimization = new CpuCoreParkingOptimization();
        ChangeSet original = PowerSchemeTestData.PowerSettingChangeSet(
            optimization.Descriptor.Id,
            PowerSchemeTestData.BalancedSchemeGuid,
            originalAcValue: 25);

        OperationOutcome outcome = await optimization.RestoreAsync(fixture.Context, original);

        Assert.True(outcome.Success);
        Assert.True(outcome.Verified);
        Assert.Equal(3, fixture.Processes.Invocations.Count);
        Assert.Equal(
            [
                "/setacvalueindex",
                PowerSchemeTestData.BalancedSchemeGuid,
                PowerSchemeTestData.ProcessorSubgroupGuid,
                PowerSchemeTestData.CoreParkingSettingGuid,
                "25",
            ],
            fixture.Processes.Invocations[0].Arguments);
        Assert.Equal(
            ["/setactive", PowerSchemeTestData.BalancedSchemeGuid],
            fixture.Processes.Invocations[1].Arguments);
        Assert.Equal(
            ["/query", "SCHEME_CURRENT", PowerSchemeTestData.ProcessorSubgroupGuid, PowerSchemeTestData.CoreParkingSettingGuid],
            fixture.Processes.Invocations[2].Arguments);
    }

    [Fact]
    public async Task Restore_WhenMetadataIsMissing_ReturnsFailureWithoutInvokingPowerCfg()
    {
        var fixture = Fixture();
        var optimization = new CpuCoreParkingOptimization();
        ChangeSet original = PowerSchemeTestData.ChangeSetWithoutMetadata(optimization.Descriptor.Id);

        OperationOutcome outcome = await optimization.RestoreAsync(fixture.Context, original);

        Assert.False(outcome.Success);
        Assert.Contains("backup", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Processes.Invocations);
    }

    [Fact]
    public async Task Restore_WhenMetadataGuidIsInvalid_ReturnsFailure()
    {
        var fixture = Fixture();
        var optimization = new CpuCoreParkingOptimization();
        var original = new ChangeSet
        {
            OptimizationId = optimization.Descriptor.Id,
            Entries = [],
            Metadata = new Dictionary<string, string>
            {
                ["activeSchemeGuid"] = "not-a-guid",
                ["originalAcValue"] = "25",
            },
        };

        OperationOutcome outcome = await optimization.RestoreAsync(fixture.Context, original);

        Assert.False(outcome.Success);
        Assert.Contains("invalid", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Processes.Invocations);
    }

    [Fact]
    public async Task Restore_WhenSetAcValueIndexFails_ReturnsFailure()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(
            string.Empty,
            setAcValueSucceeds: false);
        var optimization = new CpuCoreParkingOptimization();

        OperationOutcome outcome = await optimization.RestoreAsync(
            fixture.Context,
            PowerSchemeTestData.PowerSettingChangeSet(
                optimization.Descriptor.Id,
                PowerSchemeTestData.BalancedSchemeGuid,
                originalAcValue: 25));

        Assert.False(outcome.Success);
        Assert.Single(fixture.Processes.Invocations);
    }

    [Fact]
    public async Task Restore_WhenVerifiedValueDiffers_ReturnsFailure()
    {
        var fixture = Fixture();
        fixture.Processes.Handler = PowerSchemeTestData.Handler(PowerSchemeTestData.EnglishQuery(
            PowerSchemeTestData.ProcessorSubgroupGuid,
            PowerSchemeTestData.CoreParkingSettingGuid,
            acValue: 50));
        var optimization = new CpuCoreParkingOptimization();

        OperationOutcome outcome = await optimization.RestoreAsync(
            fixture.Context,
            PowerSchemeTestData.PowerSettingChangeSet(
                optimization.Descriptor.Id,
                PowerSchemeTestData.BalancedSchemeGuid,
                originalAcValue: 25));

        Assert.False(outcome.Success);
        Assert.Contains("not restored", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllMethods_HonorPreCancelledToken()
    {
        var fixture = Fixture();
        var optimization = new CpuCoreParkingOptimization();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        CancellationToken token = cancellation.Token;
        ChangeSet original = PowerSchemeTestData.PowerSettingChangeSet(
            optimization.Descriptor.Id,
            PowerSchemeTestData.BalancedSchemeGuid,
            originalAcValue: 25);

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
