using ValorantOptimizer.Diagnostics;
using Xunit;

namespace ValorantOptimizer.Diagnostics.Tests;

public sealed class CachedValorantLocatorTests
{
    private static ValorantInstallation Installation() => new()
    {
        ExecutablePath = @"C:\Riot Games\VALORANT\live\VALORANT.exe",
        InstallDirectory = @"C:\Riot Games\VALORANT",
        DetectionSource = "test",
    };

    private sealed class CountingLocator : IValorantLocator
    {
        private readonly Func<Task<ValorantInstallation>> _factory;

        public CountingLocator(Func<Task<ValorantInstallation>> factory)
        {
            _factory = factory;
        }

        public int Calls { get; private set; }

        public Task<ValorantInstallation> LocateAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return _factory();
        }
    }

    [Fact]
    public async Task Second_lookup_inside_the_ttl_uses_the_cache()
    {
        var inner = new CountingLocator(() => Task.FromResult(Installation()));
        var cached = new CachedValorantLocator(inner, TimeSpan.FromSeconds(30));

        ValorantInstallation first = await cached.LocateAsync();
        ValorantInstallation second = await cached.LocateAsync();

        Assert.Same(first, second);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Invalidate_forces_a_new_inner_lookup()
    {
        var inner = new CountingLocator(() => Task.FromResult(Installation()));
        var cached = new CachedValorantLocator(inner, TimeSpan.FromSeconds(30));

        await cached.LocateAsync();
        cached.Invalidate();
        await cached.LocateAsync();

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Concurrent_lookups_share_one_inner_call()
    {
        var release = new TaskCompletionSource<ValorantInstallation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new CountingLocator(() => release.Task);
        var cached = new CachedValorantLocator(inner, TimeSpan.FromSeconds(30));

        Task<ValorantInstallation>[] lookups =
        [
            cached.LocateAsync(),
            cached.LocateAsync(),
            cached.LocateAsync(),
        ];

        release.SetResult(Installation());
        await Task.WhenAll(lookups);

        Assert.Equal(1, inner.Calls);
        Assert.All(lookups, lookup => Assert.True(lookup.Result.IsFound));
    }
}
