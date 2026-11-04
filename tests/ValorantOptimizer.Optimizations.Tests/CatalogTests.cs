using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Diagnostics;
using Xunit;

namespace ValorantOptimizer.Optimizations.Tests;

public sealed class OptimizationCatalogTests
{
    private sealed class EmptyLocator : IValorantLocator
    {
        public Task<ValorantInstallation> LocateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ValorantInstallation());
    }

    private static IReadOnlyList<IOptimization> Catalog() =>
        OptimizationCatalog.CreateDefault(new EmptyLocator());

    [Fact]
    public void Catalog_contains_the_six_mvp_modules_in_order()
    {
        string[] ids = Catalog().Select(optimization => optimization.Descriptor.Id).ToArray();

        Assert.Equal(
            [
                "mouse-acceleration",
                "windows-game-mode",
                "game-bar-capture",
                "delivery-optimization-peer",
                "valorant-gpu-preference",
                "power-plan-high-performance",
            ],
            ids);
    }

    [Fact]
    public void Catalog_passes_engine_validation()
    {
        Assert.Empty(OptimizationCatalogValidator.Validate(Catalog()));
    }

    [Fact]
    public void Every_module_has_rich_descriptor_text()
    {
        foreach (IOptimization optimization in Catalog())
        {
            OptimizationDescriptor descriptor = optimization.Descriptor;
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Name));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Summary));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.WhatItChanges));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Why));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.StateWhenApplied));
        }
    }

    [Fact]
    public void Advanced_system_modules_declare_elevation()
    {
        var byId = Catalog().ToDictionary(optimization => optimization.Descriptor.Id);

        Assert.True(byId["delivery-optimization-peer"].Descriptor.RequiresElevation);
        Assert.True(byId["power-plan-high-performance"].Descriptor.RequiresElevation);
        Assert.False(byId["mouse-acceleration"].Descriptor.RequiresElevation);
    }

    [Fact]
    public void Gpu_preference_is_per_user_and_scoped_to_the_directx_key()
    {
        IOptimization gpu = Catalog().Single(optimization =>
            optimization.Descriptor.Id == "valorant-gpu-preference");

        Assert.False(gpu.Descriptor.RequiresElevation);
        Assert.Contains(
            gpu.Descriptor.ManagedScopes,
            scope => scope.Hive == RegistryHive.CurrentUser &&
                     scope.SubKey.Contains("UserGpuPreferences", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Power_plan_uses_metadata_only_and_declares_no_registry_scope()
    {
        IOptimization power = Catalog().Single(optimization =>
            optimization.Descriptor.Id == "power-plan-high-performance");

        Assert.Empty(power.Descriptor.ManagedScopes);
    }
}
