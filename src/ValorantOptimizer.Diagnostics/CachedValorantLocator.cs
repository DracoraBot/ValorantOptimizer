namespace ValorantOptimizer.Diagnostics;

/// <summary>
/// Caches a VALORANT installation lookup for a short time. The scanner, the GPU preference,
/// app-compatibility and config-review modules all need the same path within one scan; without
/// sharing, each performs the same multi-drive/process search. The cache is read-only state and
/// is deliberately short-lived so a moved installation is picked up on the next scan.
/// </summary>
public sealed class CachedValorantLocator : IValorantLocator
{
    private readonly IValorantLocator _inner;
    private readonly TimeSpan _timeToLive;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ValorantInstallation? _cached;
    private DateTimeOffset _cachedAt;

    public CachedValorantLocator(IValorantLocator inner, TimeSpan? timeToLive = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _timeToLive = timeToLive ?? TimeSpan.FromSeconds(10);
        if (_timeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeToLive), "Cache lifetime must be positive.");
        }
    }

    public async Task<ValorantInstallation> LocateAsync(CancellationToken cancellationToken = default)
    {
        ValorantInstallation? cached = Volatile.Read(ref _cached);
        if (cached is not null && DateTimeOffset.UtcNow - _cachedAt < _timeToLive)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _cached);
            if (cached is not null && DateTimeOffset.UtcNow - _cachedAt < _timeToLive)
            {
                return cached;
            }

            ValorantInstallation result = await _inner.LocateAsync(cancellationToken).ConfigureAwait(false);
            _cachedAt = DateTimeOffset.UtcNow;
            Volatile.Write(ref _cached, result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the cached installation, forcing the next lookup to run.</summary>
    public void Invalidate()
    {
        Volatile.Write(ref _cached, null);
        _cachedAt = default;
    }
}
