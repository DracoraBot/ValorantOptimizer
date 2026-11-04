using System.Globalization;
using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Diagnostics;
using ValorantOptimizer.Optimizations;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests.SystemTweaks;

/// <summary>Shared fixtures and assertions for the SystemTweaks optimization tests.</summary>
internal static class SystemTweaksTestHarness
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
        RestartRequirement expectedRestart,
        params RegistryScope[] expectedScopes)
    {
        OptimizationDescriptor descriptor = optimization.Descriptor;

        Assert.Equal(expectedId, descriptor.Id);
        Assert.Equal(expectedCategory, descriptor.Category);
        Assert.Equal(expectedRisk, descriptor.Risk);
        Assert.Equal(expectedRequiresElevation, descriptor.RequiresElevation);
        Assert.Equal(expectedRestart, descriptor.Restart);
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

    public static void SeedBuildNumber(FakeRegistryStore registry, string buildNumber) =>
        registry.SetString(
            RegistryHive.LocalMachine,
            HagsOptimization.WindowsVersionSubKey,
            HagsOptimization.BuildNumberValueName,
            buildNumber);
}

/// <summary>Deterministic VALORANT locator for tests; no filesystem or registry access.</summary>
internal sealed class FakeValorantLocator : IValorantLocator
{
    public FakeValorantLocator(ValorantInstallation installation) => Installation = installation;

    public ValorantInstallation Installation { get; set; }

    public int Calls { get; private set; }

    public CancellationToken LastCancellationToken { get; private set; }

    public Task<ValorantInstallation> LocateAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        LastCancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Installation);
    }
}
