using ValorantOptimizer.Diagnostics;

namespace ValorantOptimizer.Diagnostics.Tests;

/// <summary>Deterministic stand-in for the Win32 hardware probes.</summary>
internal sealed class FakeSystemProbe : ISystemProbe
{
    public ulong? TotalMemoryBytes { get; set; }

    public int? PhysicalCoreCount { get; set; }

    public DisplayInfo? PrimaryDisplay { get; set; }

    public bool? Elevated { get; set; }

    public ulong? GetTotalMemoryBytes() => TotalMemoryBytes;

    public int? GetPhysicalCoreCount() => PhysicalCoreCount;

    public DisplayInfo? GetPrimaryDisplay() => PrimaryDisplay;

    public bool? GetIsElevated() => Elevated;
}

internal sealed class StubSystemScanner : ISystemScanner
{
    private readonly SystemSnapshot _snapshot;

    public StubSystemScanner(SystemSnapshot snapshot) => _snapshot = snapshot;

    public Task<SystemSnapshot> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_snapshot);
    }
}

internal sealed class StubValorantLocator : IValorantLocator
{
    private readonly ValorantInstallation _installation;

    public StubValorantLocator(ValorantInstallation installation) => _installation = installation;

    public Task<ValorantInstallation> LocateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_installation);
    }
}

internal static class TestSnapshots
{
    public static SystemSnapshot Create() => new()
    {
        OsName = "Windows 11 Pro",
        OsVersion = "10.0.26100",
        OsBuild = "26100.2314",
        OsDisplayVersion = "24H2",
        Architecture = "X64",
        Cpu = new CpuInfo("Test CPU", 8, 16, 4200),
        Gpus = [new GpuInfo("Test GPU", "TestVendor", "1.2.3", "1-1-2025", 8UL * 1024 * 1024 * 1024)],
        TotalMemoryBytes = 32UL * 1024 * 1024 * 1024,
        PrimaryDisplay = new DisplayInfo(@"\\.\DISPLAY1", 2560, 1440, 240, 32),
        IsElevated = false,
        Warnings = [],
    };
}

internal static class TestFiles
{
    /// <summary>Creates a file (and parents) so existence checks resolve on disk.</summary>
    public static string CreateFile(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, "test payload");
        return path;
    }

    public static string CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
