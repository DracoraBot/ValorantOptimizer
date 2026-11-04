using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Diagnostics;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Diagnostics.Tests;

public sealed class SystemMetricsTests
{
    private const string NvidiaCsv = "NVIDIA GeForce RTX 4070 Laptop GPU, 37, 56, 4096, 8188";

    // ---------- nvidia-smi CSV parsing ----------

    [Fact]
    public void TryParseNvidiaSmiOutput_parses_a_standard_line()
    {
        bool parsed = SystemMetricsProvider.TryParseNvidiaSmiOutput(
            NvidiaCsv,
            out GpuTelemetry? telemetry,
            out string? note);

        Assert.True(parsed);
        Assert.NotNull(telemetry);
        Assert.Equal("NVIDIA GeForce RTX 4070 Laptop GPU", telemetry.Name);
        Assert.Equal(37, telemetry.UsagePercent);
        Assert.Equal(56, telemetry.TemperatureC);
        Assert.Equal(4096UL * 1024 * 1024, telemetry.MemoryUsedBytes);
        Assert.Equal(8188UL * 1024 * 1024, telemetry.MemoryTotalBytes);
        Assert.Null(note);
    }

    [Fact]
    public void TryParseNvidiaSmiOutput_tolerates_spaces_units_and_quoted_names()
    {
        const string line = "  \"NVIDIA GeForce RTX 3060\"  ,  12 %  ,  45 C ,  1024 MiB ,  12288 MiB ";

        bool parsed = SystemMetricsProvider.TryParseNvidiaSmiOutput(line, out GpuTelemetry? telemetry, out _);

        Assert.True(parsed);
        Assert.NotNull(telemetry);
        Assert.Equal("NVIDIA GeForce RTX 3060", telemetry.Name);
        Assert.Equal(12, telemetry.UsagePercent);
        Assert.Equal(45, telemetry.TemperatureC);
        Assert.Equal(1073741824UL, telemetry.MemoryUsedBytes);
        Assert.Equal(12884901888UL, telemetry.MemoryTotalBytes);
    }

    [Fact]
    public void TryParseNvidiaSmiOutput_keeps_usable_metrics_when_others_are_na()
    {
        const string line = "NVIDIA GeForce RTX 2050, [N/A], [N/A], 512, 4096";

        bool parsed = SystemMetricsProvider.TryParseNvidiaSmiOutput(line, out GpuTelemetry? telemetry, out string? note);

        Assert.True(parsed);
        Assert.NotNull(telemetry);
        Assert.Null(telemetry.UsagePercent);
        Assert.Null(telemetry.TemperatureC);
        Assert.Equal(512UL * 1024 * 1024, telemetry.MemoryUsedBytes);
        Assert.Equal(4096UL * 1024 * 1024, telemetry.MemoryTotalBytes);
        Assert.Contains("N/A", note);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("a,b,c")]
    public void TryParseNvidiaSmiOutput_rejects_empty_and_malformed_output(string? stdout)
    {
        bool parsed = SystemMetricsProvider.TryParseNvidiaSmiOutput(stdout, out GpuTelemetry? telemetry, out string? note);

        Assert.False(parsed);
        Assert.Null(telemetry);
        Assert.False(string.IsNullOrWhiteSpace(note));
    }

    [Fact]
    public void TryParseNvidiaSmiOutput_reads_only_the_first_gpu_line()
    {
        const string output = "GPU One, 10, 40, 100, 1000\nGPU Two, 90, 80, 900, 1000";

        bool parsed = SystemMetricsProvider.TryParseNvidiaSmiOutput(output, out GpuTelemetry? telemetry, out string? note);

        Assert.True(parsed);
        Assert.Equal("GPU One", telemetry!.Name);
        Assert.Equal(10, telemetry.UsagePercent);
        Assert.Contains("Multiple NVIDIA GPUs", note);
    }

    [Theory]
    [InlineData("150, 56, 4096, 8188", 100)]
    [InlineData("-20, 56, 4096, 8188", 0)]
    public void TryParseNvidiaSmiOutput_clamps_utilization_to_the_valid_range(
        string metrics,
        double expectedUsage)
    {
        SystemMetricsProvider.TryParseNvidiaSmiOutput(
            "NVIDIA GeForce RTX 4070, " + metrics,
            out GpuTelemetry? telemetry,
            out _);

        Assert.Equal(expectedUsage, telemetry!.UsagePercent);
    }

    [Fact]
    public void TryParseNumericMetric_handles_units_and_unavailable_markers()
    {
        Assert.Equal(12, SystemMetricsProvider.TryParseNumericMetric("12 %"));
        Assert.Equal(45, SystemMetricsProvider.TryParseNumericMetric("45 C"));
        Assert.Equal(4096, SystemMetricsProvider.TryParseNumericMetric("4096 MiB"));
        Assert.Null(SystemMetricsProvider.TryParseNumericMetric("[N/A]"));
        Assert.Null(SystemMetricsProvider.TryParseNumericMetric("not supported"));
        Assert.Null(SystemMetricsProvider.TryParseNumericMetric(""));
    }

    [Fact]
    public void TryParseMebibytes_converts_mebibytes_to_bytes()
    {
        Assert.Equal(4294967296UL, SystemMetricsProvider.TryParseMebibytes("4096"));
        Assert.Equal(1073741824UL, SystemMetricsProvider.TryParseMebibytes("1024 MiB"));
        Assert.Null(SystemMetricsProvider.TryParseMebibytes("[N/A]"));
        Assert.Null(SystemMetricsProvider.TryParseMebibytes("-5"));
    }

    // ---------- CPU computation ----------

    [Fact]
    public void ComputeCpuUsage_uses_kernel_plus_user_minus_idle_deltas()
    {
        double usage = SystemMetricsProvider.ComputeCpuUsage(
            previousIdle: 100,
            previousKernel: 200,
            previousUser: 100,
            currentIdle: 150,
            currentKernel: 300,
            currentUser: 200);

        Assert.Equal(75, usage, precision: 3);
    }

    [Fact]
    public void ComputeCpuUsage_zero_deltas_returns_zero()
    {
        double usage = SystemMetricsProvider.ComputeCpuUsage(100, 200, 100, 100, 200, 100);

        Assert.Equal(0, usage);
    }

    [Fact]
    public void ComputeCpuUsage_idle_only_delta_returns_zero()
    {
        double usage = SystemMetricsProvider.ComputeCpuUsage(0, 0, 0, 200, 200, 0);

        Assert.Equal(0, usage);
    }

    [Fact]
    public void ComputeCpuUsage_handles_regressed_counters()
    {
        double usage = SystemMetricsProvider.ComputeCpuUsage(200, 300, 200, 100, 200, 100);

        Assert.Equal(0, usage);
    }

    [Fact]
    public void ComputeCpuUsage_does_not_report_inconsistent_idle_counters_as_busy()
    {
        double usage = SystemMetricsProvider.ComputeCpuUsage(0, 0, 0, 500, 100, 100);

        Assert.Equal(0, usage);
    }

    [Fact]
    public void ComputeCpuUsage_record_overload_matches_the_scalar_overload()
    {
        var previous = new SystemCpuTimes(Idle: 100, Kernel: 200, User: 100);
        var current = new SystemCpuTimes(Idle: 150, Kernel: 300, User: 200);

        Assert.Equal(
            SystemMetricsProvider.ComputeCpuUsage(100, 200, 100, 150, 300, 200),
            SystemMetricsProvider.ComputeCpuUsage(previous, current));
    }

    // ---------- Memory computation ----------

    [Fact]
    public void ComputeMemoryUsed_subtracts_available_from_total()
    {
        Assert.Equal(25769803776UL, SystemMetricsProvider.ComputeMemoryUsed(34359738368, 8589934592));
    }

    [Fact]
    public void ComputeMemoryUsed_never_goes_negative()
    {
        Assert.Equal(0UL, SystemMetricsProvider.ComputeMemoryUsed(100, 200));
    }

    // ---------- Provider behavior with fakes ----------

    [Fact]
    public void Provider_is_parameterless_constructible()
    {
        var provider = new SystemMetricsProvider();

        Assert.NotNull(provider);
    }

    [Fact]
    public async Task SampleAsync_returns_a_coherent_snapshot_and_tracks_cpu_between_calls()
    {
        var cpuSamples = new Queue<SystemCpuTimes>(
        [
            new SystemCpuTimes(Idle: 100, Kernel: 200, User: 100),
            new SystemCpuTimes(Idle: 150, Kernel: 300, User: 200),
        ]);
        var memory = new SystemMemoryStatus(TotalBytes: 34359738368, AvailableBytes: 8589934592);
        var runner = new FakeProcessRunner
        {
            Handler = (_, _) => new ProcessResult(0, NvidiaCsv, string.Empty),
        };
        var provider = new SystemMetricsProvider(
            runner,
            registry: null,
            readCpuTimes: () => cpuSamples.Count > 0
                ? cpuSamples.Dequeue()
                : new SystemCpuTimes(Idle: 150, Kernel: 300, User: 200),
            readMemoryStatus: () => memory);

        DateTimeOffset before = DateTimeOffset.Now;
        SystemMetrics first = await provider.SampleAsync();
        DateTimeOffset after = DateTimeOffset.Now;

        Assert.InRange(first.SampledAt, before, after);
        Assert.Equal(0, first.CpuUsagePercent);
        Assert.Equal(34359738368UL, first.MemoryTotalBytes);
        Assert.Equal(25769803776UL, first.MemoryUsedBytes);
        Assert.Equal(37, first.GpuUsagePercent);
        Assert.Equal(56, first.GpuTemperatureC);
        Assert.Equal(4096UL * 1024 * 1024, first.GpuMemoryUsedBytes);
        Assert.Equal(8188UL * 1024 * 1024, first.GpuMemoryTotalBytes);
        Assert.Equal("NVIDIA GeForce RTX 4070 Laptop GPU", first.GpuName);
        Assert.Null(first.GpuNote);

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("nvidia-smi.exe", invocation.FileName);
        Assert.Contains(
            "--query-gpu=name,utilization.gpu,temperature.gpu,memory.used,memory.total",
            invocation.Arguments);
        Assert.Contains("--format=csv,noheader,nounits", invocation.Arguments);

        SystemMetrics second = await provider.SampleAsync();
        Assert.Equal(75, second.CpuUsagePercent, precision: 3);
    }

    [Fact]
    public async Task SampleAsync_reports_null_gpu_fields_and_a_note_when_nvidia_smi_fails()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (_, _) => new ProcessResult(1, string.Empty, "driver unavailable"),
        };
        var provider = new SystemMetricsProvider(
            runner,
            registry: null,
            readCpuTimes: () => new SystemCpuTimes(0, 0, 0),
            readMemoryStatus: () => new SystemMemoryStatus(1024, 512));

        SystemMetrics metrics = await provider.SampleAsync();

        Assert.Null(metrics.GpuUsagePercent);
        Assert.Null(metrics.GpuTemperatureC);
        Assert.Null(metrics.GpuMemoryUsedBytes);
        Assert.Null(metrics.GpuMemoryTotalBytes);
        Assert.Null(metrics.GpuName);
        Assert.StartsWith("NVIDIA telemetry unavailable", metrics.GpuNote);
        Assert.Contains("exited with code 1", metrics.GpuNote);
        Assert.Equal(SystemMetricsProvider.GetNvidiaSmiCandidates().Count, runner.Invocations.Count);
        Assert.Equal(512UL, metrics.MemoryUsedBytes);
        Assert.Equal(1024UL, metrics.MemoryTotalBytes);
    }

    [Fact]
    public async Task SampleAsync_falls_back_to_the_next_nvidia_smi_location_after_a_process_error()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (fileName, _) =>
            {
                if (fileName == "nvidia-smi.exe")
                {
                    throw new InvalidOperationException("not on PATH");
                }

                return new ProcessResult(0, NvidiaCsv, string.Empty);
            },
        };
        var provider = new SystemMetricsProvider(
            runner,
            registry: null,
            readCpuTimes: () => new SystemCpuTimes(0, 0, 0),
            readMemoryStatus: () => new SystemMemoryStatus(1024, 512));

        SystemMetrics metrics = await provider.SampleAsync();

        Assert.Equal("NVIDIA GeForce RTX 4070 Laptop GPU", metrics.GpuName);
        Assert.Equal(37, metrics.GpuUsagePercent);
        Assert.True(runner.Invocations.Count >= 2);
        Assert.Equal("nvidia-smi.exe", runner.Invocations[0].FileName);
        Assert.NotEqual("nvidia-smi.exe", runner.Invocations[1].FileName);
    }

    [Fact]
    public async Task SampleAsync_adds_registry_evidence_when_nvidia_telemetry_is_unavailable()
    {
        var registry = new FakeRegistryStore();
        string adapterKey = SystemScanner.DisplayClassKey + @"\0000";
        registry.SetString(RegistryHive.LocalMachine, adapterKey, "DriverDesc", "NVIDIA GeForce RTX 4070");
        var runner = new FakeProcessRunner
        {
            Handler = (_, _) => new ProcessResult(1, string.Empty, "no driver"),
        };
        var provider = new SystemMetricsProvider(
            runner,
            registry,
            readCpuTimes: () => new SystemCpuTimes(0, 0, 0),
            readMemoryStatus: () => new SystemMemoryStatus(1024, 512));

        SystemMetrics metrics = await provider.SampleAsync();

        Assert.Null(metrics.GpuName);
        Assert.Contains("NVIDIA telemetry unavailable", metrics.GpuNote);
        Assert.Contains("Registry reports", metrics.GpuNote);
        Assert.Contains("NVIDIA GeForce RTX 4070", metrics.GpuNote);
    }

    [Fact]
    public async Task SampleAsync_survives_unavailable_cpu_and_memory_probes()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (_, _) => new ProcessResult(1, string.Empty, "no driver"),
        };
        var provider = new SystemMetricsProvider(
            runner,
            registry: null,
            readCpuTimes: () => null,
            readMemoryStatus: () => null);

        SystemMetrics metrics = await provider.SampleAsync();

        Assert.Equal(0, metrics.CpuUsagePercent);
        Assert.Equal(0UL, metrics.MemoryUsedBytes);
        Assert.Equal(0UL, metrics.MemoryTotalBytes);
        Assert.Null(metrics.GpuUsagePercent);
        Assert.False(string.IsNullOrWhiteSpace(metrics.GpuNote));
    }

    [Fact]
    public async Task SampleAsync_survives_throwing_cpu_and_memory_probes()
    {
        var runner = new FakeProcessRunner();
        var provider = new SystemMetricsProvider(
            runner,
            registry: null,
            readCpuTimes: () => throw new InvalidOperationException("cpu probe failed"),
            readMemoryStatus: () => throw new InvalidOperationException("memory probe failed"));

        SystemMetrics metrics = await provider.SampleAsync();

        Assert.Equal(0, metrics.CpuUsagePercent);
        Assert.Equal(0UL, metrics.MemoryTotalBytes);
        Assert.Null(metrics.GpuName);
    }

    [Fact]
    public async Task SampleAsync_reports_partial_gpu_metrics_with_a_note()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (_, _) => new ProcessResult(0, "NVIDIA GeForce RTX 2050, 10, [N/A], 512, 4096", string.Empty),
        };
        var provider = new SystemMetricsProvider(
            runner,
            registry: null,
            readCpuTimes: () => new SystemCpuTimes(0, 0, 0),
            readMemoryStatus: () => new SystemMemoryStatus(1024, 512));

        SystemMetrics metrics = await provider.SampleAsync();

        Assert.Equal(10, metrics.GpuUsagePercent);
        Assert.Null(metrics.GpuTemperatureC);
        Assert.Contains("N/A", metrics.GpuNote);
    }

    [Fact]
    public async Task SampleAsync_reports_a_note_when_nvidia_smi_output_cannot_be_parsed()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (_, _) => new ProcessResult(0, "not,a,valid,telemetry", string.Empty),
        };
        var provider = new SystemMetricsProvider(
            runner,
            registry: null,
            readCpuTimes: () => new SystemCpuTimes(0, 0, 0),
            readMemoryStatus: () => new SystemMemoryStatus(1024, 512));

        SystemMetrics metrics = await provider.SampleAsync();

        Assert.Null(metrics.GpuName);
        Assert.Contains("NVIDIA telemetry unavailable", metrics.GpuNote);
        Assert.Contains("expected 5", metrics.GpuNote);
    }

    [Fact]
    public async Task SampleAsync_times_out_a_hung_nvidia_smi_without_failing()
    {
        var runner = new HangingProcessRunner();
        var provider = new SystemMetricsProvider(
            runner,
            registry: null,
            readCpuTimes: () => new SystemCpuTimes(0, 0, 0),
            readMemoryStatus: () => new SystemMemoryStatus(1024, 512),
            gpuCommandTimeout: TimeSpan.FromMilliseconds(25));

        SystemMetrics metrics = await provider.SampleAsync();

        Assert.Null(metrics.GpuName);
        Assert.Null(metrics.GpuUsagePercent);
        Assert.Contains("timed out", metrics.GpuNote);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task SampleAsync_honors_a_pre_cancelled_token_without_probing_gpu()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new FakeProcessRunner();
        var provider = new SystemMetricsProvider(
            runner,
            registry: null,
            readCpuTimes: () => new SystemCpuTimes(0, 0, 0),
            readMemoryStatus: () => new SystemMemoryStatus(1024, 512));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SampleAsync(cancellation.Token));
        Assert.Empty(runner.Invocations);
    }

    // ---------- Static configuration ----------

    [Fact]
    public void GetNvidiaSmiCandidates_includes_the_required_locations()
    {
        IReadOnlyList<string> candidates = SystemMetricsProvider.GetNvidiaSmiCandidates();
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.Contains("nvidia-smi.exe", candidates);
        Assert.Contains(
            Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
            candidates);
        Assert.Contains(Path.Combine(windows, "System32", "nvidia-smi.exe"), candidates);
    }

    [Fact]
    public void NvidiaSmiArguments_request_the_expected_columns_and_format()
    {
        Assert.Contains(
            "--query-gpu=name,utilization.gpu,temperature.gpu,memory.used,memory.total",
            SystemMetricsProvider.NvidiaSmiArguments);
        Assert.Contains("--format=csv,noheader,nounits", SystemMetricsProvider.NvidiaSmiArguments);
    }

    private sealed class HangingProcessRunner : IProcessRunner
    {
        public int Calls { get; private set; }

        public async Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new ProcessResult(0, string.Empty, string.Empty);
        }
    }
}
