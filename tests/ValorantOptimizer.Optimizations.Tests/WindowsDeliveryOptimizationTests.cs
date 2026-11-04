using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Optimizations;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests.Windows;

public sealed class DeliveryOptimizationPeerOptimizationTests
{
    private const string ConfigKey = DeliveryOptimizationPeerOptimization.ConfigSubKey;
    private const string Value = DeliveryOptimizationPeerOptimization.ValueName;
    private const string PolicyKey = DeliveryOptimizationPeerOptimization.PolicySubKey;
    private const string PolicyDownloadMode = DeliveryOptimizationPeerOptimization.PolicyDownloadModeValueName;
    private const string PolicyLegacyDownloadMode = DeliveryOptimizationPeerOptimization.PolicyLegacyDownloadModeValueName;
    private const string ServiceKey = DeliveryOptimizationPeerOptimization.ServiceSubKey;

    [Fact]
    public void Descriptor_IsStableAndFullyDescribed()
    {
        var optimization = new DeliveryOptimizationPeerOptimization();

        WindowsTestHarness.AssertWellFormedDescriptor(
            optimization,
            "delivery-optimization-peer",
            OptimizationCategory.Windows,
            RiskLevel.Advanced,
            expectedRequiresElevation: true,
            new RegistryScope(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config"));

        Assert.Equal("Peer downloads off", optimization.Descriptor.StateWhenApplied);
        Assert.Equal("Disable Delivery Optimization peer downloads", optimization.Descriptor.Name);
        Assert.DoesNotContain("fps", optimization.Descriptor.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            optimization.Descriptor.ManagedScopes,
            scope => string.Equals(scope.SubKey, PolicyKey, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Assess_HttpOnlyMode_ReturnsApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.LocalMachine, ConfigKey, Value, 0);
        var optimization = new DeliveryOptimizationPeerOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.Applied, assessment.State);
        Assert.Equal("Peer downloads disabled", assessment.Headline);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == Value &&
                        evidence.Value.Contains("HTTP only", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, registry.WriteCount - 1); // only the seeding write above
        Assert.Equal(0, registry.DeleteCount);
    }

    [Fact]
    public async Task Assess_NonZeroMode_ReturnsNotApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.LocalMachine, ConfigKey, Value, 1);
        var optimization = new DeliveryOptimizationPeerOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains("mode 1", assessment.Headline);
        Assert.False(string.IsNullOrWhiteSpace(assessment.Explanation));
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Value.StartsWith("1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Assess_ConfigValueMissingButServicePresent_ReturnsDefaultNotApplied()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.CreateKey(RegistryHive.LocalMachine, ConfigKey);
        registry.CreateKey(RegistryHive.LocalMachine, ServiceKey);
        var optimization = new DeliveryOptimizationPeerOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains("default", assessment.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public async Task Assess_NoDeliveryOptimizationKeys_ReturnsUnsupported()
    {
        OptimizationContext context = WindowsTestHarness.CreateContext(out FakeRegistryStore registry);
        var optimization = new DeliveryOptimizationPeerOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.Unsupported, assessment.State);
        Assert.Equal("Delivery Optimization not available", assessment.Headline);
        Assert.Contains("not present", assessment.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(assessment.IsActionable);
        Assert.Equal(0, registry.WriteCount);
        Assert.Equal(0, registry.DeleteCount);
    }

    [Fact]
    public async Task Assess_PolicyDownloadModeNonZero_ReturnsReviewRequired()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.LocalMachine, ConfigKey, Value, 0);
        registry.SetDword(RegistryHive.LocalMachine, PolicyKey, PolicyDownloadMode, 1);
        var optimization = new DeliveryOptimizationPeerOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.ReviewRequired, assessment.State);
        Assert.Equal("Managed by policy", assessment.Headline);
        Assert.Contains("managed by policy", assessment.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DODownloadMode", assessment.BlockedReason);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == $"Policy {PolicyDownloadMode}");
        Assert.False(assessment.IsActionable);
    }

    [Fact]
    public async Task Assess_LegacyPolicyDownloadModeNonZero_ReturnsReviewRequired()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.LocalMachine, ConfigKey, Value, 0);
        registry.SetDword(RegistryHive.LocalMachine, PolicyKey, PolicyLegacyDownloadMode, 3);
        var optimization = new DeliveryOptimizationPeerOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.ReviewRequired, assessment.State);
        Assert.Contains("DownloadMode", assessment.BlockedReason);
        Assert.Contains("3", assessment.BlockedReason);
    }

    [Fact]
    public async Task Assess_PolicyZero_DoesNotBlockLocalChange()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.LocalMachine, ConfigKey, Value, 2);
        registry.SetDword(RegistryHive.LocalMachine, PolicyKey, PolicyDownloadMode, 0);
        var optimization = new DeliveryOptimizationPeerOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.NotApplied, assessment.State);
        Assert.Contains(
            assessment.Evidence,
            evidence => evidence.Label == $"Policy {PolicyDownloadMode}" &&
                        evidence.Value.StartsWith("0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Assess_PolicyValueWithUnexpectedKind_ReturnsReviewRequired()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.LocalMachine, ConfigKey, Value, 0);
        registry.SetString(RegistryHive.LocalMachine, PolicyKey, PolicyDownloadMode, "1");
        var optimization = new DeliveryOptimizationPeerOptimization();

        OptimizationAssessment assessment = await optimization.AssessAsync(context);

        Assert.Equal(OptimizationState.ReviewRequired, assessment.State);
        Assert.Contains("cannot be determined", assessment.BlockedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Capture_OnlyRecordsLocalConfigValueAndDoesNotWrite()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.LocalMachine, ConfigKey, Value, 2);
        registry.SetDword(RegistryHive.LocalMachine, PolicyKey, PolicyDownloadMode, 1);
        int writesAfterSeeding = registry.WriteCount;
        var optimization = new DeliveryOptimizationPeerOptimization();

        ChangeSet original = await optimization.CaptureAsync(context);

        RegistrySnapshotEntry entry = Assert.Single(original.Entries);
        Assert.Equal("delivery-optimization-peer", original.OptimizationId);
        Assert.Equal(RegistryHive.LocalMachine, entry.Hive);
        Assert.Equal(ConfigKey, entry.SubKey);
        Assert.Equal(Value, entry.ValueName);
        Assert.True(entry.Existed);
        Assert.Equal(RegistryValueKind.DWord, entry.Kind);
        Assert.Equal("2", entry.SerializedData);
        Assert.Equal(writesAfterSeeding, registry.WriteCount);
        Assert.Equal(0, registry.DeleteCount);
    }

    [Fact]
    public async Task Apply_WritesHttpOnlyDword()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.LocalMachine, ConfigKey, Value, 3);
        var optimization = new DeliveryOptimizationPeerOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);

        OperationOutcome outcome = await optimization.ApplyAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Single(outcome.Changes);
        Assert.Equal(0, WindowsTestHarness.ReadDword(registry, RegistryHive.LocalMachine, ConfigKey, Value));
        Assert.DoesNotContain(
            registry.Operations,
            operation => operation.Contains(PolicyKey, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Apply_WhenPolicyManaged_WritesNeitherConfigNorPolicy()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry, isElevated: true);
        registry.SetDword(RegistryHive.LocalMachine, ConfigKey, Value, 1);
        registry.SetDword(RegistryHive.LocalMachine, PolicyKey, PolicyDownloadMode, 1);
        int writesAfterSeeding = registry.WriteCount;
        var optimization = new DeliveryOptimizationPeerOptimization();
        ChangeSet original = new() { OptimizationId = "delivery-optimization-peer" };

        OperationOutcome outcome = await optimization.ApplyAsync(context, original);

        Assert.False(outcome.Success);
        Assert.Contains("policy", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(writesAfterSeeding, registry.WriteCount);
        Assert.Equal(1, WindowsTestHarness.ReadDword(registry, RegistryHive.LocalMachine, ConfigKey, Value));
        Assert.Equal(1, WindowsTestHarness.ReadDword(registry, RegistryHive.LocalMachine, PolicyKey, PolicyDownloadMode));
    }

    [Fact]
    public async Task Apply_WhenCancelled_WritesNothing()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.LocalMachine, ConfigKey, Value, 3);
        int writesAfterSeeding = registry.WriteCount;
        var optimization = new DeliveryOptimizationPeerOptimization();
        ChangeSet original = new() { OptimizationId = "delivery-optimization-peer" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => optimization.ApplyAsync(context, original, cts.Token));

        Assert.Equal(writesAfterSeeding, registry.WriteCount);
    }

    [Fact]
    public async Task Restore_RestoresOriginalModeAndNeverTouchesPolicy()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.SetDword(RegistryHive.LocalMachine, ConfigKey, Value, 2);
        registry.SetDword(RegistryHive.LocalMachine, PolicyKey, PolicyDownloadMode, 0);
        int operationsAfterSeeding = registry.Operations.Count;
        var optimization = new DeliveryOptimizationPeerOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);
        await optimization.ApplyAsync(context, original);

        OperationOutcome outcome = await optimization.RestoreAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Equal(2, WindowsTestHarness.ReadDword(registry, RegistryHive.LocalMachine, ConfigKey, Value));
        Assert.Equal(0, WindowsTestHarness.ReadDword(registry, RegistryHive.LocalMachine, PolicyKey, PolicyDownloadMode));
        Assert.DoesNotContain(
            registry.Operations.Skip(operationsAfterSeeding),
            operation => operation.Contains(PolicyKey, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Restore_DeletesValueThatWasOriginallyAbsent()
    {
        FakeRegistryStore registry = new();
        OptimizationContext context = WindowsTestHarness.CreateContext(registry);
        registry.CreateKey(RegistryHive.LocalMachine, ServiceKey);
        var optimization = new DeliveryOptimizationPeerOptimization();
        ChangeSet original = await optimization.CaptureAsync(context);
        RegistrySnapshotEntry captured = Assert.Single(original.Entries);
        Assert.False(captured.Existed);

        await optimization.ApplyAsync(context, original);
        Assert.NotNull(registry.ReadValue(RegistryHive.LocalMachine, ConfigKey, Value));

        OperationOutcome outcome = await optimization.RestoreAsync(context, original);

        Assert.True(outcome.Success);
        Assert.Null(registry.ReadValue(RegistryHive.LocalMachine, ConfigKey, Value));
        Assert.Equal(1, registry.DeleteCount);
    }
}
