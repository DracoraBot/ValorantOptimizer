using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Diagnostics;

/// <summary>
/// Read-only live telemetry sampler: CPU from <c>GetSystemTimes</c>, memory from
/// <c>GlobalMemoryStatusEx</c> and NVIDIA GPU usage/temperature/memory from <c>nvidia-smi</c>
/// (launched through <see cref="IProcessRunner"/>). Never requires administrator rights, never
/// writes, and never throws for a failed probe: unavailable GPU data yields null fields plus a
/// human-readable <see cref="SystemMetrics.GpuNote"/>. The provider keeps no timer, so the caller
/// controls the polling cadence.
/// </summary>
public sealed class SystemMetricsProvider : ISystemMetricsProvider
{
    internal static readonly string[] NvidiaSmiArguments =
    [
        "--query-gpu=name,utilization.gpu,temperature.gpu,memory.used,memory.total",
        "--format=csv,noheader,nounits",
    ];

    internal static readonly TimeSpan DefaultGpuCommandTimeout = TimeSpan.FromMilliseconds(1200);

    private const double Mebibyte = 1024.0 * 1024.0;

    private static readonly string[] MetricUnitSuffixes =
        ["mib", "gib", "mhz", "mb", "gb", "%", "°c", "c", "w"];

    private readonly IProcessRunner _processes;
    private readonly IRegistryStore? _registry;
    private readonly Func<SystemCpuTimes?> _readCpuTimes;
    private readonly Func<SystemMemoryStatus?> _readMemoryStatus;
    private readonly TimeSpan _gpuCommandTimeout;
    private readonly object _cpuLock = new();
    private SystemCpuTimes? _previousCpuTimes;

    /// <summary>Production provider: real Win32 probes and <see cref="ProcessRunner"/>.</summary>
    public SystemMetricsProvider()
        : this(new ProcessRunner(), new RegistryStore())
    {
    }

    /// <summary>Test seam: inject the process runner, registry and (optionally) the CPU/RAM probes.</summary>
    internal SystemMetricsProvider(
        IProcessRunner processes,
        IRegistryStore? registry = null,
        Func<SystemCpuTimes?>? readCpuTimes = null,
        Func<SystemMemoryStatus?>? readMemoryStatus = null,
        TimeSpan? gpuCommandTimeout = null)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _registry = registry;
        _readCpuTimes = readCpuTimes ?? ReadNativeCpuTimes;
        _readMemoryStatus = readMemoryStatus ?? ReadNativeMemoryStatus;
        _gpuCommandTimeout = gpuCommandTimeout ?? DefaultGpuCommandTimeout;
    }

    public async Task<SystemMetrics> SampleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset sampledAt = DateTimeOffset.Now;

        double cpuUsagePercent = SampleCpuUsage();
        (ulong memoryUsedBytes, ulong memoryTotalBytes) = SampleMemory();
        GpuQueryResult gpu = await QueryGpuAsync(cancellationToken).ConfigureAwait(false);

        return new SystemMetrics(
            sampledAt,
            Math.Clamp(cpuUsagePercent, 0, 100),
            memoryUsedBytes,
            memoryTotalBytes,
            gpu.UsagePercent,
            gpu.TemperatureC,
            gpu.MemoryUsedBytes,
            gpu.MemoryTotalBytes,
            gpu.Name,
            gpu.Note);
    }

    // ---------- CPU ----------

    /// <summary>
    /// First call has no baseline and returns 0; subsequent calls use the delta since the previous
    /// sample. A failed probe also returns 0 instead of failing the sample.
    /// </summary>
    private double SampleCpuUsage()
    {
        SystemCpuTimes? current;
        try
        {
            current = _readCpuTimes();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }

        if (current is not { } now)
        {
            return 0;
        }

        lock (_cpuLock)
        {
            if (_previousCpuTimes is not { } previous)
            {
                _previousCpuTimes = now;
                return 0;
            }

            _previousCpuTimes = now;
            return ComputeCpuUsage(previous, now);
        }
    }

    /// <summary>
    /// Computes busy time from cumulative kernel/user/idle counters. Kernel time includes idle
    /// time on Windows, so busy = (kernelDelta + userDelta) - idleDelta.
    /// </summary>
    internal static double ComputeCpuUsage(in SystemCpuTimes previous, in SystemCpuTimes current) =>
        ComputeCpuUsage(previous.Idle, previous.Kernel, previous.User, current.Idle, current.Kernel, current.User);

    internal static double ComputeCpuUsage(
        ulong previousIdle,
        ulong previousKernel,
        ulong previousUser,
        ulong currentIdle,
        ulong currentKernel,
        ulong currentUser)
    {
        ulong idleDelta = Delta(currentIdle, previousIdle);
        ulong kernelDelta = Delta(currentKernel, previousKernel);
        ulong userDelta = Delta(currentUser, previousUser);
        ulong totalDelta = kernelDelta + userDelta;

        if (totalDelta == 0)
        {
            return 0;
        }

        // Defensive: idle can never exceed total, but do not trust inconsistent counters.
        ulong idle = Math.Min(idleDelta, totalDelta);
        double busy = totalDelta - idle;
        return Math.Clamp(busy / totalDelta * 100.0, 0, 100);
    }

    private static ulong Delta(ulong current, ulong previous) => current >= previous ? current - previous : 0;

    // ---------- Memory ----------

    private (ulong UsedBytes, ulong TotalBytes) SampleMemory()
    {
        try
        {
            if (_readMemoryStatus() is not { } status || status.TotalBytes == 0)
            {
                return (0, 0);
            }

            return (ComputeMemoryUsed(status.TotalBytes, status.AvailableBytes), status.TotalBytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (0, 0);
        }
    }

    /// <summary>Used memory = total - available, never negative.</summary>
    internal static ulong ComputeMemoryUsed(ulong totalBytes, ulong availableBytes) =>
        availableBytes >= totalBytes ? 0 : totalBytes - availableBytes;

    // ---------- GPU ----------

    private async Task<GpuQueryResult> QueryGpuAsync(CancellationToken cancellationToken)
    {
        var failures = new List<string>();

        foreach (string executable in GetNvidiaSmiCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_gpuCommandTimeout);

            try
            {
                ProcessResult result = await _processes
                    .RunAsync(executable, NvidiaSmiArguments, timeout.Token)
                    .ConfigureAwait(false);

                if (!result.Succeeded)
                {
                    failures.Add($"{DescribeExecutable(executable)} exited with code {result.ExitCode}");
                    continue;
                }

                if (TryParseNvidiaSmiOutput(result.StandardOutput, out GpuTelemetry? telemetry, out string? parseNote) &&
                    telemetry is not null)
                {
                    return new GpuQueryResult(
                        telemetry.UsagePercent,
                        telemetry.TemperatureC,
                        telemetry.MemoryUsedBytes,
                        telemetry.MemoryTotalBytes,
                        telemetry.Name,
                        parseNote);
                }

                failures.Add($"{DescribeExecutable(executable)}: {parseNote ?? "output was not usable"}");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Our per-command timeout fired; do not let a hanging driver stall the sample.
                failures.Add(
                    $"{DescribeExecutable(executable)} timed out after {_gpuCommandTimeout.TotalMilliseconds:F0} ms");
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{DescribeExecutable(executable)}: {ex.Message}");
            }
        }

        return GpuQueryResult.Unavailable(BuildGpuUnavailableNote(failures));
    }

    /// <summary>The well-known nvidia-smi locations, in fast-to-slow fallback order.</summary>
    internal static IReadOnlyList<string> GetNvidiaSmiCandidates()
    {
        var candidates = new List<string> { "nvidia-smi.exe" };

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"));
        }

        string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            candidates.Add(Path.Combine(systemRoot, "System32", "nvidia-smi.exe"));
        }

        return candidates;
    }

    private string BuildGpuUnavailableNote(IReadOnlyList<string> failures)
    {
        var note = new StringBuilder("NVIDIA telemetry unavailable");
        if (failures.Count > 0)
        {
            note.Append(": ");
            note.Append(string.Join("; ", failures.Distinct(StringComparer.Ordinal).Take(2)));
        }

        note.Append('.');

        string? adapterName = TryFindNvidiaAdapterName();
        if (adapterName is not null)
        {
            note.Append($" Registry reports '{adapterName}'.");
        }

        return note.ToString();
    }

    /// <summary>
    /// Optional supporting evidence from the display class registry key: distinguishes "no NVIDIA
    /// adapter installed" from "NVIDIA adapter present but telemetry unavailable".
    /// </summary>
    private string? TryFindNvidiaAdapterName()
    {
        if (_registry is null)
        {
            return null;
        }

        try
        {
            foreach (string subKey in _registry.GetSubKeyNames(RegistryHive.LocalMachine, SystemScanner.DisplayClassKey))
            {
                if (!int.TryParse(subKey, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    continue;
                }

                RegistryValue? value = _registry.ReadValue(
                    RegistryHive.LocalMachine,
                    SystemScanner.DisplayClassKey + "\\" + subKey,
                    "DriverDesc");

                if (value?.Data is string name &&
                    name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    return name.Trim();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        return null;
    }

    private static string DescribeExecutable(string executable)
    {
        string fileName = Path.GetFileName(executable);
        return string.IsNullOrWhiteSpace(fileName) ? executable : fileName;
    }

    // ---------- nvidia-smi CSV parsing ----------

    /// <summary>
    /// Parses the first non-empty line of
    /// <c>--query-gpu=name,utilization.gpu,temperature.gpu,memory.used,memory.total --format=csv,noheader,nounits</c>.
    /// Tolerates extra whitespace, quoted fields, unit suffixes ("%", "MiB", "C") and <c>[N/A]</c>
    /// metrics. Memory values are MiB and converted to bytes.
    /// </summary>
    internal static bool TryParseNvidiaSmiOutput(
        string? standardOutput,
        out GpuTelemetry? telemetry,
        out string? note)
    {
        telemetry = null;
        note = null;

        if (string.IsNullOrWhiteSpace(standardOutput))
        {
            note = "nvidia-smi returned no output";
            return false;
        }

        string[] lines = standardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            note = "nvidia-smi returned no output";
            return false;
        }

        string[] fields = lines[0].Split(',');
        if (fields.Length < 5)
        {
            note = $"nvidia-smi output had {fields.Length} fields; expected 5";
            return false;
        }

        string name = NormalizeCsvField(fields[0]);
        if (name.Length == 0)
        {
            note = "nvidia-smi output did not include a GPU name";
            return false;
        }

        double? usage = TryParseNumericMetric(fields[1]);
        double? temperature = TryParseNumericMetric(fields[2]);
        ulong? memoryUsed = TryParseMebibytes(fields[3]);
        ulong? memoryTotal = TryParseMebibytes(fields[4]);

        if (usage is null && temperature is null && memoryUsed is null && memoryTotal is null)
        {
            note = "nvidia-smi output did not include usable GPU metrics";
            return false;
        }

        if (usage is not null)
        {
            usage = Math.Clamp(usage.Value, 0, 100);
        }

        telemetry = new GpuTelemetry(name, usage, temperature, memoryUsed, memoryTotal);

        var missing = new List<string>();
        if (usage is null)
        {
            missing.Add("utilization");
        }

        if (temperature is null)
        {
            missing.Add("temperature");
        }

        if (memoryUsed is null)
        {
            missing.Add("memory usage");
        }

        if (memoryTotal is null)
        {
            missing.Add("total memory");
        }

        if (missing.Count > 0)
        {
            note = $"nvidia-smi reported N/A for {string.Join(", ", missing)}.";
        }

        if (lines.Length > 1)
        {
            note = string.IsNullOrEmpty(note)
                ? "Multiple NVIDIA GPUs were reported; showing the first."
                : note + " Multiple NVIDIA GPUs were reported; showing the first.";
        }

        return true;
    }

    internal static string NormalizeCsvField(string? field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return string.Empty;
        }

        string value = field.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        return value.Trim();
    }

    internal static double? TryParseNumericMetric(string? field)
    {
        string cleaned = NormalizeCsvField(field);
        if (cleaned.Length == 0 || IsNotAvailable(cleaned))
        {
            return null;
        }

        cleaned = StripUnitSuffix(cleaned);
        if (!double.TryParse(
                cleaned,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out double value))
        {
            return null;
        }

        return double.IsFinite(value) ? value : null;
    }

    /// <summary>Parses an nvidia-smi MiB memory field into bytes.</summary>
    internal static ulong? TryParseMebibytes(string? field)
    {
        if (TryParseNumericMetric(field) is not { } mebibytes || mebibytes < 0)
        {
            return null;
        }

        double bytes = mebibytes * Mebibyte;
        if (!double.IsFinite(bytes) || bytes > ulong.MaxValue)
        {
            return null;
        }

        return (ulong)Math.Round(bytes, MidpointRounding.AwayFromZero);
    }

    private static bool IsNotAvailable(string value) =>
        value.Equals("[N/A]", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("NA", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Not Supported", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("[Not Supported]", StringComparison.OrdinalIgnoreCase);

    private static string StripUnitSuffix(string value)
    {
        foreach (string suffix in MetricUnitSuffixes)
        {
            if (value.Length > suffix.Length && value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                string candidate = value[..^suffix.Length].TrimEnd();
                if (candidate.Length > 0)
                {
                    return candidate;
                }
            }
        }

        return value;
    }

    // ---------- Native probes ----------

    private static SystemCpuTimes? ReadNativeCpuTimes()
    {
        try
        {
            if (!NativeMethods.GetSystemTimes(
                    out NativeMethods.FileTime idle,
                    out NativeMethods.FileTime kernel,
                    out NativeMethods.FileTime user))
            {
                return null;
            }

            return new SystemCpuTimes(ToTicks(idle), ToTicks(kernel), ToTicks(user));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static ulong ToTicks(NativeMethods.FileTime value) =>
        ((ulong)value.HighDateTime << 32) | value.LowDateTime;

    private static SystemMemoryStatus? ReadNativeMemoryStatus()
    {
        try
        {
            var status = new NativeMethods.MemoryStatusEx
            {
                Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>(),
            };

            if (!NativeMethods.GlobalMemoryStatusEx(ref status))
            {
                return null;
            }

            return new SystemMemoryStatus(status.TotalPhys, status.AvailablePhys);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private sealed record GpuQueryResult(
        double? UsagePercent,
        double? TemperatureC,
        ulong? MemoryUsedBytes,
        ulong? MemoryTotalBytes,
        string? Name,
        string? Note)
    {
        public static GpuQueryResult Unavailable(string note) =>
            new(null, null, null, null, null, note);
    }
}

/// <summary>Cumulative CPU counters (100 ns units) as reported by GetSystemTimes.</summary>
internal readonly record struct SystemCpuTimes(ulong Idle, ulong Kernel, ulong User);

/// <summary>Physical memory totals in bytes as reported by GlobalMemoryStatusEx.</summary>
internal readonly record struct SystemMemoryStatus(ulong TotalBytes, ulong AvailableBytes);

/// <summary>Parsed nvidia-smi values; metrics can be null when the driver reports [N/A].</summary>
internal sealed record GpuTelemetry(
    string Name,
    double? UsagePercent,
    double? TemperatureC,
    ulong? MemoryUsedBytes,
    ulong? MemoryTotalBytes);
