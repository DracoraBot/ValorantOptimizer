using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ValorantOptimizer.App.Infrastructure;
using ValorantOptimizer.App.Presentation;
using ValorantOptimizer.Diagnostics;

namespace ValorantOptimizer.App.ViewModels;

/// <summary>
/// Live, read-only telemetry for the Monitoring page. FPS and frame times need a game-capture
/// integration that is not bundled, so they are explicitly marked unavailable instead of guessed.
/// </summary>
public sealed class MonitoringViewModel : ObservableObject
{
    private readonly ISystemMetricsProvider _provider;
    private readonly DispatcherTimer _timer;
    private readonly Queue<double> _cpuHistory = new();
    private readonly Queue<double> _gpuHistory = new();
    private readonly Queue<double> _memoryHistory = new();
    private bool _sampling;
    private bool _isRunning;
    private int _ticks;
    private double _cpuPercent;
    private double _gpuPercent;
    private double _memoryPercent;
    private string _cpuText = "—";
    private string _gpuText = "—";
    private string _memoryText = "—";
    private string _memoryDetail = "Waiting for the first sample";
    private string _gpuNameText = "GPU telemetry";
    private string _gpuTempText = "—";
    private string _gpuTempDetail = "Waiting for NVIDIA telemetry";
    private string _lastSampleText = "Not sampled yet";
    private string _errorText = string.Empty;
    private bool _hasError;
    private bool _gpuAvailable;
    private bool _gpuTemperatureAvailable;
    private PointCollection _cpuSparkline = new();
    private PointCollection _gpuSparkline = new();
    private PointCollection _memorySparkline = new();

    public MonitoringViewModel(ISystemMetricsProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await SampleAsync();
        ToggleCommand = new RelayCommand(ToggleRunning);
    }

    public ICommand ToggleCommand { get; }

    public ObservableCollection<ProcessMemoryRowViewModel> TopProcesses { get; } = [];

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(ToggleButtonText));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public string ToggleButtonText => IsRunning ? "Pause sampling" : "Resume sampling";

    public string StatusText => IsRunning ? "LIVE" : "PAUSED";

    public Brush StatusBrush => IsRunning ? UiTheme.Success : UiTheme.TextMuted;

    public double CpuPercent
    {
        get => _cpuPercent;
        private set
        {
            if (SetProperty(ref _cpuPercent, value))
            {
                OnPropertyChanged(nameof(CpuPercentText));
            }
        }
    }

    public string CpuPercentText => $"{CpuPercent:0}%";

    public string CpuText
    {
        get => _cpuText;
        private set => SetProperty(ref _cpuText, value);
    }

    public double GpuPercent
    {
        get => _gpuPercent;
        private set
        {
            if (SetProperty(ref _gpuPercent, value))
            {
                OnPropertyChanged(nameof(GpuPercentText));
            }
        }
    }

    public string GpuPercentText => GpuAvailable ? $"{GpuPercent:0}%" : "N/A";

    public bool GpuAvailable
    {
        get => _gpuAvailable;
        private set
        {
            if (SetProperty(ref _gpuAvailable, value))
            {
                OnPropertyChanged(nameof(GpuPercentText));
                OnPropertyChanged(nameof(GpuTileOpacity));
            }
        }
    }

    public double GpuTileOpacity => GpuAvailable ? 1.0 : 0.55;

    public string GpuText
    {
        get => _gpuText;
        private set => SetProperty(ref _gpuText, value);
    }

    public string GpuNameText
    {
        get => _gpuNameText;
        private set => SetProperty(ref _gpuNameText, value);
    }

    public double MemoryPercent
    {
        get => _memoryPercent;
        private set
        {
            if (SetProperty(ref _memoryPercent, value))
            {
                OnPropertyChanged(nameof(MemoryPercentText));
            }
        }
    }

    public string MemoryPercentText => $"{MemoryPercent:0}%";

    public string MemoryText
    {
        get => _memoryText;
        private set => SetProperty(ref _memoryText, value);
    }

    public string MemoryDetail
    {
        get => _memoryDetail;
        private set => SetProperty(ref _memoryDetail, value);
    }

    public string GpuTempText
    {
        get => _gpuTempText;
        private set => SetProperty(ref _gpuTempText, value);
    }

    public string GpuTempDetail
    {
        get => _gpuTempDetail;
        private set => SetProperty(ref _gpuTempDetail, value);
    }

    public double GpuTempTileOpacity => GpuTemperatureAvailable ? 1.0 : 0.55;

    public bool GpuTemperatureAvailable
    {
        get => _gpuTemperatureAvailable;
        private set
        {
            if (SetProperty(ref _gpuTemperatureAvailable, value))
            {
                OnPropertyChanged(nameof(GpuTempTileOpacity));
            }
        }
    }

    public PointCollection CpuSparkline
    {
        get => _cpuSparkline;
        private set => SetProperty(ref _cpuSparkline, value);
    }

    public PointCollection GpuSparkline
    {
        get => _gpuSparkline;
        private set => SetProperty(ref _gpuSparkline, value);
    }

    public PointCollection MemorySparkline
    {
        get => _memorySparkline;
        private set => SetProperty(ref _memorySparkline, value);
    }

    public string LastSampleText
    {
        get => _lastSampleText;
        private set => SetProperty(ref _lastSampleText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public string FpsText => "Not measured";

    public string FpsDetail => "Requires PresentMon or ETW frame capture — not bundled.";

    public string FrameTimeText => "Not measured";

    public string FrameTimeDetail => "Frame pacing needs per-frame capture from the game.";

    public string CpuTemperatureText => "Not available";

    public string CpuTemperatureDetail => "Requires a hardware sensor service; no generic Windows API.";

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        _timer.Start();
        _ = SampleAsync();
        RefreshTopProcesses();
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        _timer.Stop();
    }

    private void ToggleRunning()
    {
        if (IsRunning)
        {
            Stop();
        }
        else
        {
            Start();
        }
    }

    private async Task SampleAsync()
    {
        if (_sampling)
        {
            return;
        }

        _sampling = true;
        try
        {
            SystemMetrics metrics = await _provider.SampleAsync();
            Apply(metrics);
            _ticks++;
            if (_ticks % 3 == 0)
            {
                RefreshTopProcesses();
            }

            HasError = false;
            ErrorText = string.Empty;
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorText = "Telemetry sampling failed: " + ex.Message;
        }
        finally
        {
            _sampling = false;
        }
    }

    private void Apply(SystemMetrics metrics)
    {
        CpuPercent = Math.Clamp(metrics.CpuUsagePercent, 0, 100);
        CpuText = $"{CpuPercent:0}%";
        AppendHistory(_cpuHistory, CpuPercent);
        CpuSparkline = BuildSparkline(_cpuHistory);

        MemoryPercent = metrics.MemoryTotalBytes == 0
            ? 0
            : Math.Clamp(metrics.MemoryUsedBytes * 100.0 / metrics.MemoryTotalBytes, 0, 100);
        MemoryText = $"{MemoryPercent:0}%";
        MemoryDetail = $"{FormatBytes(metrics.MemoryUsedBytes)} of {FormatBytes(metrics.MemoryTotalBytes)} in use";
        AppendHistory(_memoryHistory, MemoryPercent);
        MemorySparkline = BuildSparkline(_memoryHistory);

        GpuAvailable = metrics.GpuUsagePercent.HasValue;
        GpuPercent = Math.Clamp(metrics.GpuUsagePercent ?? 0, 0, 100);
        GpuText = GpuAvailable ? $"{GpuPercent:0}%" : "N/A";
        GpuNameText = string.IsNullOrWhiteSpace(metrics.GpuName) ? "GPU telemetry" : metrics.GpuName!;
        if (GpuAvailable)
        {
            AppendHistory(_gpuHistory, GpuPercent);
            GpuSparkline = BuildSparkline(_gpuHistory);
        }

        GpuTemperatureAvailable = metrics.GpuTemperatureC.HasValue;
        GpuTempText = GpuTemperatureAvailable ? $"{metrics.GpuTemperatureC!.Value:0}°C" : "N/A";
        GpuTempDetail = GpuTemperatureAvailable
            ? "GPU temperature · NVIDIA telemetry"
            : metrics.GpuNote ?? "NVIDIA telemetry unavailable";

        LastSampleText = "Last sample " + metrics.SampledAt.LocalDateTime.ToString("HH:mm:ss");
    }

    private void RefreshTopProcesses()
    {
        try
        {
            var rows = new List<ProcessMemoryRowViewModel>();
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == Environment.ProcessId ||
                        string.Equals(process.ProcessName, "Idle", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    long workingSet = process.WorkingSet64;
                    if (workingSet > 0)
                    {
                        rows.Add(new ProcessMemoryRowViewModel(process.ProcessName, workingSet));
                    }
                }
                catch (Exception)
                {
                    // Access is denied for some protected processes; skip them.
                }
                finally
                {
                    process.Dispose();
                }
            }

            TopProcesses.Clear();
            foreach (ProcessMemoryRowViewModel row in rows
                         .OrderByDescending(row => row.WorkingSetBytes)
                         .Take(6))
            {
                TopProcesses.Add(row);
            }
        }
        catch (Exception)
        {
            // Process enumeration is best effort and read-only.
        }
    }

    private static void AppendHistory(Queue<double> history, double value)
    {
        history.Enqueue(value);
        while (history.Count > 60)
        {
            history.Dequeue();
        }
    }

    private static PointCollection BuildSparkline(Queue<double> history)
    {
        var points = new PointCollection(history.Count);
        if (history.Count == 0)
        {
            return points;
        }

        double step = history.Count <= 1 ? 0 : 236.0 / (history.Count - 1);
        int index = 0;
        foreach (double value in history)
        {
            double y = 38 - Math.Clamp(value, 0, 100) / 100.0 * 34;
            points.Add(new Point(index * step, y));
            index++;
        }

        return points;
    }

    private static string FormatBytes(ulong bytes)
    {
        const double gigabyte = 1024d * 1024d * 1024d;
        return bytes >= gigabyte ? $"{bytes / gigabyte:0.0} GB" : $"{bytes / (1024d * 1024d):0} MB";
    }
}

public sealed class ProcessMemoryRowViewModel
{
    public ProcessMemoryRowViewModel(string name, long workingSetBytes)
    {
        Name = name;
        WorkingSetBytes = workingSetBytes;
    }

    public string Name { get; }

    public long WorkingSetBytes { get; }

    public string MemoryText => $"{WorkingSetBytes / (1024d * 1024d):0} MB";
}
