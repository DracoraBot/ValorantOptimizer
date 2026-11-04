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
    public void Catalog_contains_all_expected_modules_in_catalog_order()
    {
        string[] ids = Catalog().Select(optimization => optimization.Descriptor.Id).ToArray();

        Assert.Equal(
            [
                "mouse-acceleration",
                "mouse-pointer-speed",
                "windows-game-mode",
                "game-bar-capture",
                "windows-background-apps",
                "delivery-optimization-peer",
                "cpu-power-throttling",
                "cpu-core-parking",
                "cpu-scheduling",
                "power-plan-high-performance",
                "gpu-hags",
                "nvidia-settings",
                "memory-overview",
                "network-adapter-power",
                "network-overview",
                "usb-selective-suspend",
                "usb-device-power",
                "storage-overview",
                "valorant-gpu-preference",
                "valorant-appcompat",
                "valorant-config",
            ],
            ids);
        Assert.Equal(21, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
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
        Assert.True(byId["cpu-power-throttling"].Descriptor.RequiresElevation);
        Assert.True(byId["cpu-core-parking"].Descriptor.RequiresElevation);
        Assert.True(byId["cpu-scheduling"].Descriptor.RequiresElevation);
        Assert.True(byId["gpu-hags"].Descriptor.RequiresElevation);
        Assert.True(byId["network-adapter-power"].Descriptor.RequiresElevation);
        Assert.True(byId["usb-selective-suspend"].Descriptor.RequiresElevation);
        Assert.False(byId["mouse-acceleration"].Descriptor.RequiresElevation);
        Assert.False(byId["valorant-appcompat"].Descriptor.RequiresElevation);
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
