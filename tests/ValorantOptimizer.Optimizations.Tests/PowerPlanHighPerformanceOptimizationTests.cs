using ValorantOptimizer.Core;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests;

public sealed class PowerPlanHighPerformanceOptimizationTests
{
    private const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";

    private sealed class Fixture
    {
        public Fixture(bool isElevated = false)
        {
            Context = new OptimizationContext(Registry, Processes, isElevated);
        }

        public FakeProcessRunner Processes { get; } = new();

        public FakeRegistryStore Registry { get; } = new();

        public OptimizationContext Context { get; }

        public PowerPlanHighPerformanceOptimization Optimization { get; } = new();

        public void UseHandler(
            string listOutput = "",
            string activeOutput = "",
            int listExitCode = 0,
            int activeExitCode = 0,
            int setActiveExitCode = 0)
        {
            Processes.Handler = (_, arguments) =>
            {
                string command = arguments.Count > 0 ? arguments[0] : string.Empty;
                return command switch
                {
                    "/list" => new ProcessResult(
                        listExitCode,
                        listExitCode == 0 ? listOutput : string.Empty,
                        listExitCode == 0 ? string.Empty : "list failed"),
                    "/getactivescheme" => new ProcessResult(
                        activeExitCode,
                        activeExitCode == 0 ? activeOutput : string.Empty,
                        activeExitCode == 0 ? string.Empty : "getactivescheme failed"),
                    "/setactive" => new ProcessResult(
                        setActiveExitCode,
                        string.Empty,
                        setActiveExitCode == 0 ? string.Empty : "setactive failed"),
                    _ => new ProcessResult(1, string.Empty, $"unexpected command '{command}'"),
                };
            };
        }
    }

    [Fact]
    public void Descriptor_HasExpectedIdentityAndSafetyMetadata()
    {
        OptimizationDescriptor descriptor = new PowerPlanHighPerformanceOptimization().Descriptor;

        Assert.Equal("power-plan-high-performance", descriptor.Id);
        Assert.Equal(OptimizationCategory.CpuPower, descriptor.Category);
        Assert.Equal(RiskLevel.Advanced, descriptor.Risk);
        Assert.True(descriptor.RequiresElevation);
        Assert.Equal(RestartRequirement.None, descriptor.Restart);
        Assert.Empty(descriptor.ManagedScopes);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Summary));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.WhatItChanges));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Why));
        Assert.Contains(
            descriptor.Notes,
            note => note.Contains("system-wide", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            descriptor.Notes,
            note => note.Contains("does not create", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Assess_EnglishOutput_WhenHighPerformanceIsActive_IsApplied()
    {
        var fixture = new Fixture();
        fixture.UseHandler(
            SchemeList((Balanced, "Balanced"), (HighPerformance, "High performance")),
            ActiveScheme(HighPerformance, "High performance"));

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
        Assert.Contains(
            assessment.Evidence,
            item => item.Value.Contains("High performance", StringComparison.Ordinal));
        Assert.Contains(
            assessment.Evidence,
            item => item.Value.Contains(HighPerformance, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(2, fixture.Processes.Invocations.Count);
        Assert.Equal("powercfg.exe", fixture.Processes.Invocations[0].FileName);
        Assert.Equal(["/list"], fixture.Processes.Invocations[0].Arguments);
        Assert.Equal(["/getactivescheme"], fixture.Processes.Invocations[1].Arguments);
    }

    [Fact]
    public async Task Assess_GermanOutput_WhenBalancedIsActive_IsNotApplied()
    {
        var fixture = new Fixture();
        fixture.UseHandler(
            "Energieschemas (* Aktiv)\r\n" +
            "-----------------------------------\r\n" +
            $"Energieschema-GUID: {Balanced}  (Ausbalanciert) *\r\n" +
            $"Energieschema-GUID: {HighPerformance}  (Höchstleistung)\r\n",
            $"Energieschema-GUID: {Balanced}  (Ausbalanciert)\r\n");

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains(
            assessment.Evidence,
            item => item.Value.Contains("Ausbalanciert", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Assess_WhenHighPerformanceIsNotListed_IsUnsupported()
    {
        var fixture = new Fixture();
        fixture.UseHandler(
            SchemeList((Balanced, "Balanced"), (PowerSaver, "Power saver")),
            ActiveScheme(Balanced, "Balanced"));

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Unsupported, assessment.State);
        Assert.Equal(
            "High performance plan is not available; Windows 11 may hide it. Do not create schemes.",
            assessment.BlockedReason);
        Assert.Contains(assessment.Evidence, item => item.Label == "High performance plan");
        Assert.DoesNotContain(
            fixture.Processes.Invocations,
            invocation => invocation.Arguments.Count > 0 && invocation.Arguments[0] == "/setactive");
    }

    [Fact]
    public async Task Assess_WhenPowerCfgListFails_IsUnknown()
    {
        var fixture = new Fixture();
        fixture.UseHandler(listExitCode: 1);

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Unknown, assessment.State);
        Assert.Contains("powercfg /list", assessment.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Assess_WhenGetActiveSchemeFails_IsUnknown()
    {
        var fixture = new Fixture();
        fixture.UseHandler(
            SchemeList((Balanced, "Balanced"), (HighPerformance, "High performance")),
            activeExitCode: 1);

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Unknown, assessment.State);
    }

    [Fact]
    public async Task Assess_WhenActiveSchemeOutputHasNoGuid_IsUnknown()
    {
        var fixture = new Fixture();
        fixture.UseHandler(
            SchemeList((Balanced, "Balanced"), (HighPerformance, "High performance")),
            "Power Scheme GUID: (no identifier)");

        OptimizationAssessment assessment = await fixture.Optimization.AssessAsync(fixture.Context);

        Assert.Equal(OptimizationState.Unknown, assessment.State);
    }

    [Fact]
    public async Task Capture_RecordsActiveSchemeMetadataOnly()
    {
        var fixture = new Fixture();
        fixture.UseHandler(activeOutput: ActiveScheme(Balanced, "Balanced"));

        ChangeSet changeSet = await fixture.Optimization.CaptureAsync(fixture.Context);

        Assert.Equal("power-plan-high-performance", changeSet.OptimizationId);
        Assert.Empty(changeSet.Entries);
        Assert.Equal(Balanced, changeSet.Metadata["activeSchemeGuid"]);
        Assert.Equal("Balanced", changeSet.Metadata["activeSchemeName"]);
        Assert.Equal(0, fixture.Registry.WriteCount);
        Assert.Equal(0, fixture.Registry.DeleteCount);
        Assert.Single(fixture.Processes.Invocations);
        Assert.Equal(["/getactivescheme"], fixture.Processes.Invocations[0].Arguments);
    }

    [Fact]
    public async Task Capture_WhenGetActiveSchemeFails_Throws()
    {
        var fixture = new Fixture();
        fixture.UseHandler(activeExitCode: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Optimization.CaptureAsync(fixture.Context));
    }

    [Fact]
    public async Task Apply_SetsHighPerformanceThenVerifies()
    {
        var fixture = new Fixture();
        fixture.UseHandler(activeOutput: ActiveScheme(HighPerformance, "High performance"));
        ChangeSet original = CaptureFixtures.Capture();

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, original);

        Assert.True(outcome.Success);
        Assert.True(outcome.Verified);
        Assert.Contains("system-wide", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no reboot", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Registry.WriteCount);

        Assert.Equal(2, fixture.Processes.Invocations.Count);
        Assert.Equal(["/setactive", HighPerformance], fixture.Processes.Invocations[0].Arguments);
        Assert.Equal(["/getactivescheme"], fixture.Processes.Invocations[1].Arguments);
    }

    [Fact]
    public async Task Apply_WhenSetActiveFails_ReturnsFailureWithoutVerifying()
    {
        var fixture = new Fixture();
        fixture.UseHandler(setActiveExitCode: 1);

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, CaptureFixtures.Capture());

        Assert.False(outcome.Success);
        Assert.Contains("did not accept", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.Processes.Invocations);
        Assert.Equal(["/setactive", HighPerformance], fixture.Processes.Invocations[0].Arguments);
    }

    [Fact]
    public async Task Apply_WhenVerificationShowsAnotherPlan_ReturnsFailure()
    {
        var fixture = new Fixture();
        fixture.UseHandler(activeOutput: ActiveScheme(Balanced, "Balanced"));

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, CaptureFixtures.Capture());

        Assert.False(outcome.Success);
        Assert.Contains("instead of High performance", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Undo", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, fixture.Processes.Invocations.Count);
    }

    [Fact]
    public async Task Apply_WhenPostChangeVerificationFails_ReturnsFailure()
    {
        var fixture = new Fixture();
        fixture.UseHandler(activeExitCode: 1);

        OperationOutcome outcome = await fixture.Optimization.ApplyAsync(fixture.Context, CaptureFixtures.Capture());

        Assert.False(outcome.Success);
        Assert.Contains("could not be verified", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CaptureThenApplyThenRestore_RestoresTheExactPreviousScheme()
    {
        var fixture = new Fixture();
        string activeScheme = Balanced;
        fixture.Processes.Handler = (_, arguments) =>
        {
            string command = arguments.Count > 0 ? arguments[0] : string.Empty;
            return command switch
            {
                "/list" => new ProcessResult(0, SchemeList(
                    (Balanced, "Balanced"),
                    (HighPerformance, "High performance")), string.Empty),
                "/getactivescheme" => new ProcessResult(
                    0,
                    ActiveScheme(
                        activeScheme,
                        activeScheme == HighPerformance ? "High performance" : "Balanced"),
                    string.Empty),
                "/setactive" => SetActive(arguments),
                _ => new ProcessResult(1, string.Empty, "unexpected command"),
            };
        };

        ChangeSet original = await fixture.Optimization.CaptureAsync(fixture.Context);
        Assert.Equal(Balanced, original.Metadata["activeSchemeGuid"]);

        OperationOutcome apply = await fixture.Optimization.ApplyAsync(fixture.Context, original);
        Assert.True(apply.Success);
        Assert.True(apply.Verified);
        Assert.Equal(HighPerformance, activeScheme);

        OperationOutcome restore = await fixture.Optimization.RestoreAsync(fixture.Context, original);
        Assert.True(restore.Success);
        Assert.True(restore.Verified);
        Assert.Equal(Balanced, activeScheme);
        Assert.Contains("Balanced", restore.Message, StringComparison.Ordinal);

        List<IReadOnlyList<string>> setActiveCalls = fixture.Processes.Invocations
            .Where(invocation => invocation.Arguments.Count > 0 && invocation.Arguments[0] == "/setactive")
            .Select(invocation => invocation.Arguments)
            .ToList();
        Assert.Equal(2, setActiveCalls.Count);
        Assert.Equal(["/setactive", HighPerformance], setActiveCalls[0]);
        Assert.Equal(["/setactive", Balanced], setActiveCalls[1]);

        Assert.Equal(0, fixture.Registry.WriteCount);
        Assert.Equal(0, fixture.Registry.DeleteCount);

        ProcessResult SetActive(IReadOnlyList<string> arguments)
        {
            activeScheme = arguments[1];
            return new ProcessResult(0, string.Empty, string.Empty);
        }
    }

    [Fact]
    public async Task Restore_WhenMetadataIsMissing_FailsWithoutLaunchingPowerCfg()
    {
        var fixture = new Fixture();
        var original = new ChangeSet
        {
            OptimizationId = "power-plan-high-performance",
            Entries = [],
        };

        OperationOutcome outcome = await fixture.Optimization.RestoreAsync(fixture.Context, original);

        Assert.False(outcome.Success);
        Assert.Contains("backup", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be restored", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Processes.Invocations);
    }

    [Fact]
    public async Task Restore_WhenMetadataGuidIsInvalid_FailsWithoutLaunchingPowerCfg()
    {
        var fixture = new Fixture();
        var original = new ChangeSet
        {
            OptimizationId = "power-plan-high-performance",
            Entries = [],
            Metadata = new Dictionary<string, string> { ["activeSchemeGuid"] = "not-a-guid" },
        };

        OperationOutcome outcome = await fixture.Optimization.RestoreAsync(fixture.Context, original);

        Assert.False(outcome.Success);
        Assert.Contains("invalid", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Processes.Invocations);
    }

    [Fact]
    public async Task Restore_WhenSetActiveFails_ReturnsFailure()
    {
        var fixture = new Fixture();
        fixture.UseHandler(setActiveExitCode: 1);
        var original = new ChangeSet
        {
            OptimizationId = "power-plan-high-performance",
            Entries = [],
            Metadata = new Dictionary<string, string> { ["activeSchemeGuid"] = Balanced },
        };

        OperationOutcome outcome = await fixture.Optimization.RestoreAsync(fixture.Context, original);

        Assert.False(outcome.Success);
        Assert.Single(fixture.Processes.Invocations);
        Assert.Equal(["/setactive", Balanced], fixture.Processes.Invocations[0].Arguments);
    }

    [Fact]
    public async Task Restore_WhenVerificationShowsAnotherPlan_ReturnsFailure()
    {
        var fixture = new Fixture();
        fixture.UseHandler(activeOutput: ActiveScheme(HighPerformance, "High performance"));
        var original = new ChangeSet
        {
            OptimizationId = "power-plan-high-performance",
            Entries = [],
            Metadata = new Dictionary<string, string> { ["activeSchemeGuid"] = Balanced },
        };

        OperationOutcome outcome = await fixture.Optimization.RestoreAsync(fixture.Context, original);

        Assert.False(outcome.Success);
        Assert.Contains("not restored", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllMethods_HonorPreCancelledToken()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        CancellationToken token = cancellation.Token;
        var original = new ChangeSet
        {
            OptimizationId = "power-plan-high-performance",
            Entries = [],
            Metadata = new Dictionary<string, string> { ["activeSchemeGuid"] = Balanced },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Optimization.AssessAsync(fixture.Context, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Optimization.CaptureAsync(fixture.Context, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Optimization.ApplyAsync(fixture.Context, original, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Optimization.RestoreAsync(fixture.Context, original, token));
    }

    private static string SchemeList(params (string Guid, string Name)[] schemes)
    {
        var lines = new List<string>
        {
            "Existing Power Schemes (* Active)",
            "-----------------------------------",
        };
        lines.AddRange(schemes.Select(scheme => $"Power Scheme GUID: {scheme.Guid}  ({scheme.Name})"));
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static string ActiveScheme(string guid, string? name, string prefix = "Power Scheme GUID") =>
        name is null ? $"{prefix}: {guid}\r\n" : $"{prefix}: {guid}  ({name})\r\n";

    private static class CaptureFixtures
    {
        public static ChangeSet Capture() => new()
        {
            OptimizationId = "power-plan-high-performance",
            Entries = [],
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["activeSchemeGuid"] = Balanced,
                ["activeSchemeName"] = "Balanced",
            },
        };
    }
}
