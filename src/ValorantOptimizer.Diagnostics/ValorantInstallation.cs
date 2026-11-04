namespace ValorantOptimizer.Diagnostics;

/// <summary>Result of locating the installed VALORANT game client.</summary>
public sealed record ValorantInstallation
{
    public string? ExecutablePath { get; init; }

    /// <summary>The shipping game binary (VALORANT-Win64-Shipping.exe) when it can be located.</summary>
    public string? ShippingExecutablePath { get; init; }

    public string? InstallDirectory { get; init; }

    public string? Version { get; init; }

    /// <summary>How the path was found, shown in the scan summary (for transparency).</summary>
    public string? DetectionSource { get; init; }

    public List<string> Notes { get; init; } = [];

    public bool IsFound => !string.IsNullOrWhiteSpace(ExecutablePath);
}

/// <summary>Locates VALORANT.exe without requiring administrator rights.</summary>
public interface IValorantLocator
{
    Task<ValorantInstallation> LocateAsync(CancellationToken cancellationToken = default);
}
