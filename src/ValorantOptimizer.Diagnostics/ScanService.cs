namespace ValorantOptimizer.Diagnostics;

/// <summary>Combines a system snapshot with VALORANT detection into one read-only scan result.</summary>
public sealed class ScanService : IScanService
{
    private readonly ISystemScanner _systemScanner;
    private readonly IValorantLocator _valorantLocator;

    public ScanService(ISystemScanner systemScanner, IValorantLocator valorantLocator)
    {
        _systemScanner = systemScanner ?? throw new ArgumentNullException(nameof(systemScanner));
        _valorantLocator = valorantLocator ?? throw new ArgumentNullException(nameof(valorantLocator));
    }

    public async Task<ScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        SystemSnapshot system = await _systemScanner.ScanAsync(cancellationToken).ConfigureAwait(false);
        ValorantInstallation valorant = await _valorantLocator.LocateAsync(cancellationToken).ConfigureAwait(false);
        return new ScanResult(system, valorant);
    }
}
