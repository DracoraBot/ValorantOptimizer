using System.Globalization;
using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Optimizations;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests.Windows;

/// <summary>Shared fixtures and assertions for the Windows optimization module tests.</summary>
internal static class WindowsTestHarness
{
    public static OptimizationContext CreateContext(FakeRegistryStore registry, bool isElevated = false) =>
        new(registry, new FakeProcessRunner(), isElevated);

    public static OptimizationContext CreateContext(out FakeRegistryStore registry, bool isElevated = false)
    {
        registry = new FakeRegistryStore();
        return CreateContext(registry, isElevated);
    }

    public static void AssertWellFormedDescriptor(
        IOptimization optimization,
        string expectedId,
        OptimizationCategory expectedCategory,
        RiskLevel expectedRisk,
        bool expectedRequiresElevation,
        params RegistryScope[] expectedScopes)
    {
        OptimizationDescriptor descriptor = optimization.Descriptor;

        Assert.Equal(expectedId, descriptor.Id);
        Assert.Equal(expectedCategory, descriptor.Category);
        Assert.Equal(expectedRisk, descriptor.Risk);
        Assert.Equal(expectedRequiresElevation, descriptor.RequiresElevation);
        Assert.Equal(RestartRequirement.None, descriptor.Restart);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Name));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Summary));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.WhatItChanges));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Why));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Compatibility));
        Assert.False(string.IsNullOrWhiteSpace(descriptor.StateWhenApplied));
        Assert.NotEmpty(descriptor.Notes);
        Assert.Equal(expectedScopes, descriptor.ManagedScopes);
        Assert.Empty(OptimizationCatalogValidator.Validate([optimization]));
    }

    public static int ReadDword(IRegistryStore registry, RegistryHive hive, string subKey, string valueName)
    {
        RegistryValue? value = registry.ReadValue(hive, subKey, valueName);
        Assert.NotNull(value);
        Assert.Equal(RegistryValueKind.DWord, value.Kind);
        return Convert.ToInt32(value.Data, CultureInfo.InvariantCulture);
    }
}

/// <summary>Deterministic fake for the native mouse-settings applier.</summary>
internal sealed class FakeMouseAccelerationApplier : IMouseAccelerationApplier
{
    public int CallCount { get; private set; }

    public int LastSpeed { get; private set; }

    public int LastThreshold1 { get; private set; }

    public int LastThreshold2 { get; private set; }

    public bool Succeeds { get; set; } = true;

    public bool TryApply(int speed, int threshold1, int threshold2)
    {
        CallCount++;
        LastSpeed = speed;
        LastThreshold1 = threshold1;
        LastThreshold2 = threshold2;
        return Succeeds;
    }
}
