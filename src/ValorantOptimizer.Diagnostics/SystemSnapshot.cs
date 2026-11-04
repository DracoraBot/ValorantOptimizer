namespace ValorantOptimizer.Diagnostics;

public sealed record CpuInfo(
    string Name,
    int? PhysicalCores,
    int LogicalProcessors,
    int? MaxClockMhz);

public sealed record GpuInfo(
    string Name,
    string? Vendor,
    string? DriverVersion,
    string? DriverDate,
    ulong? MemoryBytes,
    bool IsSoftwareAdapter = false)
{
    public bool IsNvidia =>
        Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
        Vendor?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true;

    public bool IsAmd =>
        Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
}

public sealed record DisplayInfo(
    string? DeviceName,
    int Width,
    int Height,
    int RefreshHz,
    int BitsPerPixel);

/// <summary>Read-only snapshot of the machine. Never contains secrets.</summary>
public sealed record SystemSnapshot
{
    public required string OsName { get; init; }

    /// <summary>e.g. "10.0.26100".</summary>
    public required string OsVersion { get; init; }

    /// <summary>e.g. "26100.2314".</summary>
    public required string OsBuild { get; init; }

    /// <summary>e.g. "24H2" when available.</summary>
    public string? OsDisplayVersion { get; init; }

    public required string Architecture { get; init; }

    public required CpuInfo Cpu { get; init; }

    public List<GpuInfo> Gpus { get; init; } = [];

    public required ulong TotalMemoryBytes { get; init; }

    public DisplayInfo? PrimaryDisplay { get; init; }

    public required bool IsElevated { get; init; }

    public DateTimeOffset ScannedAt { get; init; } = DateTimeOffset.Now;

    public List<string> Warnings { get; init; } = [];

    public GpuInfo? PrimaryGpu =>
        Gpus.FirstOrDefault(gpu => !gpu.IsSoftwareAdapter) ?? Gpus.FirstOrDefault();

    public bool HasNvidiaGpu => Gpus.Any(gpu => gpu.IsNvidia && !gpu.IsSoftwareAdapter);
}

public interface ISystemScanner
{
    Task<SystemSnapshot> ScanAsync(CancellationToken cancellationToken = default);
}

public sealed record ScanResult(SystemSnapshot System, ValorantInstallation Valorant);

public interface IScanService
{
    Task<ScanResult> ScanAsync(CancellationToken cancellationToken = default);
}
