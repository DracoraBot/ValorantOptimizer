using ValorantOptimizer.Diagnostics;
using Xunit;

namespace ValorantOptimizer.Diagnostics.Tests;

public sealed class ScanServiceTests
{
    [Fact]
    public async Task ScanAsync_combines_the_system_snapshot_and_the_valorant_installation()
    {
        SystemSnapshot snapshot = TestSnapshots.Create();
        var installation = new ValorantInstallation
        {
            ExecutablePath = @"C:\Riot Games\VALORANT\live\VALORANT.exe",
            ShippingExecutablePath = @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe",
            InstallDirectory = @"C:\Riot Games\VALORANT\live",
            Version = "1.2.3",
            DetectionSource = ValorantLocator.DetectionSourceProductSettings,
        };

        var service = new ScanService(
            new StubSystemScanner(snapshot),
            new StubValorantLocator(installation));

        ScanResult result = await service.ScanAsync();

        Assert.Same(snapshot, result.System);
        Assert.Same(installation, result.Valorant);
        Assert.True(result.Valorant.IsFound);
        Assert.Equal("Windows 11 Pro", result.System.OsName);
    }

    [Fact]
    public async Task ScanAsync_propagates_cancellation_to_the_scan_steps()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var service = new ScanService(
            new StubSystemScanner(TestSnapshots.Create()),
            new StubValorantLocator(new ValorantInstallation()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ScanAsync(cancellation.Token));
    }

    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        var scanner = new StubSystemScanner(TestSnapshots.Create());
        var locator = new StubValorantLocator(new ValorantInstallation());

        Assert.Throws<ArgumentNullException>(() => new ScanService(null!, locator));
        Assert.Throws<ArgumentNullException>(() => new ScanService(scanner, null!));
    }
}
