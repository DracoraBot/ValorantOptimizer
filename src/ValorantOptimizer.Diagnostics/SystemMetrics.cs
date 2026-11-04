namespace ValorantOptimizer.Diagnostics;

/// <summary>
/// One read-only live sample of machine load. GPU fields are nullable because NVIDIA telemetry
/// (nvidia-smi) is optional and may be unavailable on AMD/Intel systems or when the driver does
/// not expose it; <see cref="GpuNote"/> then carries the human-readable reason.
/// </summary>
public sealed record SystemMetrics(
    DateTimeOffset SampledAt,
    double CpuUsagePercent,
    ulong MemoryUsedBytes,
    ulong MemoryTotalBytes,
    double? GpuUsagePercent,
    double? GpuTemperatureC,
    ulong? GpuMemoryUsedBytes,
    ulong? GpuMemoryTotalBytes,
    string? GpuName,
    string? GpuNote);

/// <summary>
/// Samples live CPU, memory and (when available) NVIDIA GPU telemetry. Read-only, no
/// administrator rights, no writes, and no timer: the caller decides the polling cadence.
/// </summary>
public interface ISystemMetricsProvider
{
    Task<SystemMetrics> SampleAsync(CancellationToken cancellationToken = default);
}
