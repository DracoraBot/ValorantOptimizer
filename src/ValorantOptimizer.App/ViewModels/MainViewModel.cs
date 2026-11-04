using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ValorantOptimizer.App.Infrastructure;
using ValorantOptimizer.App.Presentation;
using ValorantOptimizer.Core;
using ValorantOptimizer.Diagnostics;

namespace ValorantOptimizer.App.ViewModels;

/// <summary>Shell view model: scan summary, navigation, optimization grid, apply/undo flow, history.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IScanService _scanService;
    private readonly IStartupAppScanner _startupScanner;
    private readonly OptimizationEngine _engine;
    private readonly IActionLog _actionLog;
    private readonly AppPaths _paths;
    private readonly bool _isElevated;
    private readonly bool _isPreview;
    private readonly ICollectionView _cardsView;
    private readonly AsyncRelayCommand _rescanCommand;
    private readonly AsyncRelayCommand _confirmCommand;
    private readonly AsyncRelayCommand _refreshStartupCommand;
    private readonly AsyncRelayCommand _refreshHistoryCommand;
    private readonly AsyncRelayCommand _applySafeCommand;

    private bool _initialized;
    private bool _startupLoaded;
    private bool _historyLoaded;
    private bool _bulkRunning;
    private List<OptimizationCategory>? _categoryFilter;
    private string _stateFilter = "all";
    private Func<Task>? _pendingConfirmation;
    private List<CardViewModel> _pendingSafeCards = [];
    private NavPage _currentPage = NavPage.Optimizations;
    private string _pageTitle = "Overview";
    private string _pageSubtitle = string.Empty;
    private string _pageEyebrow = "SETUP";
    private string _searchText = string.Empty;
    private int _visibleCount;
    private bool _isScanning;
    private string _scanStatus = "Preparing scan…";
    private bool _hasScanError;
    private string _scanError = string.Empty;
    private string _lastScannedLabel = "Not scanned yet";
    private bool _isStartupLoading;
    private bool _hasStartupError;
    private string _startupError = string.Empty;
    private string _startupSummary = string.Empty;
    private bool _isHistoryLoading;
    private bool _hasHistoryError;
    private string _historyError = string.Empty;
    private string _historySummary = string.Empty;
    private CardViewModel? _selectedCard;
    private bool _isDetailOpen;
    private bool _isConfirmOpen;
    private string _confirmEyebrow = "APPLY OPTIMIZATION";
    private string _confirmTitle = string.Empty;
    private string _confirmMessage = string.Empty;
    private string _confirmRiskLabel = "SAFE";
    private Brush _confirmRiskForeground = UiTheme.Success;
    private bool _confirmShowRisk = true;
    private bool _confirmRequireAck;
    private bool _confirmAcknowledged;
    private bool _confirmIsBusy;
    private string _confirmButtonText = "Apply change";
    private string _confirmAckText = string.Empty;

    public MainViewModel(
        IScanService scanService,
        IStartupAppScanner startupScanner,
        ISystemMetricsProvider metricsProvider,
        OptimizationEngine engine,
        IActionLog actionLog,
        AppPaths paths,
        bool isElevated,
        bool isPreview)
    {
        _scanService = scanService ?? throw new ArgumentNullException(nameof(scanService));
        _startupScanner = startupScanner ?? throw new ArgumentNullException(nameof(startupScanner));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _actionLog = actionLog ?? throw new ArgumentNullException(nameof(actionLog));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _isElevated = isElevated;
        _isPreview = isPreview;
        Monitoring = new MonitoringViewModel(
            metricsProvider ?? throw new ArgumentNullException(nameof(metricsProvider)));

        Cards = [];
        CardsView = CollectionViewSource.GetDefaultView(Cards);
        _cardsView = (ICollectionView)CardsView;
        _cardsView.Filter = FilterCard;

        SummaryTiles = [];
        StartupApps = [];
        HistoryEntries = [];
        StateFilters = [];
        OverviewNav = [];
        CategoryNav = [];
        ToolNav = [];
        Toast = new ToastViewModel();

        StateFilters.Add(new FilterChipViewModel("all", "All", SelectFilter));
        StateFilters.Add(new FilterChipViewModel("applied", "Applied", SelectFilter));
        StateFilters.Add(new FilterChipViewModel("available", "Available", SelectFilter));
        StateFilters.Add(new FilterChipViewModel("attention", "Needs review", SelectFilter));
        StateFilters.Add(new FilterChipViewModel("unsupported", "Unsupported", SelectFilter));
        StateFilters[0].IsSelected = true;

        _rescanCommand = new AsyncRelayCommand(ScanAsync, () => !IsScanning);
        _confirmCommand = new AsyncRelayCommand(ConfirmAsync, () => CanConfirm);
        _refreshStartupCommand = new AsyncRelayCommand(() => LoadStartupAsync(force: true));
        _refreshHistoryCommand = new AsyncRelayCommand(LoadHistoryAsync);
        _applySafeCommand = new AsyncRelayCommand(
            () =>
            {
                RequestApplySafe();
                return Task.CompletedTask;
            },
            () => HasSafeRecommendations && !_bulkRunning);

        RescanCommand = _rescanCommand;
        ConfirmCommand = _confirmCommand;
        CancelConfirmCommand = new RelayCommand(CancelConfirm);
        RefreshStartupCommand = _refreshStartupCommand;
        RefreshHistoryCommand = _refreshHistoryCommand;
        OpenTaskManagerCommand = new RelayCommand(OpenTaskManager);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        RequestApplyCommand = new RelayCommand<CardViewModel>(RequestApply);
        RequestUndoCommand = new RelayCommand<CardViewModel>(RequestUndo);
        ToggleCommand = new RelayCommand<CardViewModel>(ToggleOptimization);
        ShowDetailsCommand = new RelayCommand<CardViewModel>(ShowDetails);
        CloseDetailsCommand = new RelayCommand(() => IsDetailOpen = false);
        DismissToastCommand = Toast.DismissCommand;
        ApplySafeCommand = _applySafeCommand;
        SelectFilterCommand = new RelayCommand<FilterChipViewModel>(SelectFilter);

        AppVersion = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0");
        DataFolderPath = _paths.DataRoot;

        BuildNavigation();
        SelectNav(OverviewNav[0], loadData: false);
        EnvironmentLabel = _isPreview ? "Preview data" : _isElevated ? "Administrator" : "Standard user";
    }

    // ------------------------------------------------------------- collections

    public ObservableCollection<CardViewModel> Cards { get; }

    public ICollectionView CardsView { get; }

    public ObservableCollection<SummaryTileViewModel> SummaryTiles { get; }

    public ObservableCollection<StartupRowViewModel> StartupApps { get; }

    public ObservableCollection<HistoryRowViewModel> HistoryEntries { get; }

    public ObservableCollection<FilterChipViewModel> StateFilters { get; }

    public ObservableCollection<NavItemViewModel> OverviewNav { get; }

    public ObservableCollection<NavItemViewModel> CategoryNav { get; }

    public ObservableCollection<NavItemViewModel> ToolNav { get; }

    public MonitoringViewModel Monitoring { get; }

    public ToastViewModel Toast { get; }

    // --------------------------------------------------------------- commands

    public ICommand RescanCommand { get; }

    public ICommand RequestApplyCommand { get; }

    public ICommand RequestUndoCommand { get; }

    public ICommand ToggleCommand { get; }

    public ICommand ShowDetailsCommand { get; }

    public ICommand CloseDetailsCommand { get; }

    public ICommand ConfirmCommand { get; }

    public ICommand CancelConfirmCommand { get; }

    public ICommand RefreshStartupCommand { get; }

    public ICommand RefreshHistoryCommand { get; }

    public ICommand OpenTaskManagerCommand { get; }

    public ICommand OpenDataFolderCommand { get; }

    public ICommand OpenLogFolderCommand { get; }

    public ICommand DismissToastCommand { get; }

    public ICommand ApplySafeCommand { get; }

    public ICommand SelectFilterCommand { get; }

    // ------------------------------------------------------------------ header

    public string AppVersion { get; }

    public string DataFolderPath { get; }

    public string EnvironmentLabel { get; }

    public Brush EnvironmentBrush => _isPreview ? UiTheme.Warning : UiTheme.Neutral;

    public string RiotDisclaimer => "Unofficial tool. Not affiliated with Riot Games.";

    public bool IsPreview => _isPreview;

    public bool IsElevated => _isElevated;

    public bool IsOptimizationPage => _currentPage == NavPage.Optimizations;

    public bool IsStartupPage => _currentPage == NavPage.Startup;

    public bool IsHistoryPage => _currentPage == NavPage.History;

    public bool IsMonitoringPage => _currentPage == NavPage.Monitoring;

    public NavPage CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(IsOptimizationPage));
                OnPropertyChanged(nameof(IsStartupPage));
                OnPropertyChanged(nameof(IsHistoryPage));
                OnPropertyChanged(nameof(IsMonitoringPage));
            }
        }
    }

    public string PageTitle
    {
        get => _pageTitle;
        private set => SetProperty(ref _pageTitle, value);
    }

    public string PageSubtitle
    {
        get => _pageSubtitle;
        private set => SetProperty(ref _pageSubtitle, value);
    }

    public string PageEyebrow
    {
        get => _pageEyebrow;
        private set => SetProperty(ref _pageEyebrow, value);
    }

    // ------------------------------------------------------------------- scan

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                _rescanCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    public string ScanStatus
    {
        get => _scanStatus;
        private set => SetProperty(ref _scanStatus, value);
    }

    public bool HasScanError
    {
        get => _hasScanError;
        private set => SetProperty(ref _hasScanError, value);
    }

    public string ScanError
    {
        get => _scanError;
        private set => SetProperty(ref _scanError, value);
    }

    public string LastScannedLabel
    {
        get => _lastScannedLabel;
        private set => SetProperty(ref _lastScannedLabel, value);
    }

    public bool HasSummaryTiles => SummaryTiles.Count > 0;

    public string AppliedCountLabel
    {
        get
        {
            int applied = Cards.Count(card => card.State == OptimizationState.Applied);
            return Cards.Count == 0 ? "No optimizations loaded" : $"{applied} of {Cards.Count} applied";
        }
    }

    // ------------------------------------------------------------------ grid

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RefreshView();
            }
        }
    }

    public int VisibleCount
    {
        get => _visibleCount;
        private set
        {
            if (SetProperty(ref _visibleCount, value))
            {
                OnPropertyChanged(nameof(VisibleCountLabel));
                OnPropertyChanged(nameof(HasNoVisibleCards));
                OnPropertyChanged(nameof(EmptyMessage));
            }
        }
    }

    public string VisibleCountLabel => VisibleCount == 1 ? "1 optimization" : $"{VisibleCount} optimizations";

    public bool HasNoVisibleCards => VisibleCount == 0;

    public bool ShowEmptyState => !IsScanning && HasNoVisibleCards;

    public string EmptyMessage
    {
        get
        {
            if (Cards.Count == 0)
            {
                return "No optimization modules were loaded.";
            }

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                return $"No optimizations match “{SearchText}”.";
            }

            if (_stateFilter != "all")
            {
                return "Nothing matches this filter. Try “All”.";
            }

            return "No optimizations in this category yet.";
        }
    }

    public bool HasSafeRecommendations => _pendingSafeCards.Count > 0;

    public string SafeRecommendationLabel => _pendingSafeCards.Count == 1
        ? "Apply 1 safe change"
        : $"Apply {_pendingSafeCards.Count} safe changes";

    // ----------------------------------------------------------------- detail

    public CardViewModel? SelectedCard
    {
        get => _selectedCard;
        private set => SetProperty(ref _selectedCard, value);
    }

    public bool IsDetailOpen
    {
        get => _isDetailOpen;
        private set => SetProperty(ref _isDetailOpen, value);
    }

    // ----------------------------------------------------------- confirmation

    public bool IsConfirmOpen
    {
        get => _isConfirmOpen;
        private set
        {
            if (SetProperty(ref _isConfirmOpen, value))
            {
                RaiseConfirmState();
            }
        }
    }

    public string ConfirmEyebrow
    {
        get => _confirmEyebrow;
        private set => SetProperty(ref _confirmEyebrow, value);
    }

    public string ConfirmTitle
    {
        get => _confirmTitle;
        private set => SetProperty(ref _confirmTitle, value);
    }

    public string ConfirmMessage
    {
        get => _confirmMessage;
        private set => SetProperty(ref _confirmMessage, value);
    }

    public string ConfirmRiskLabel
    {
        get => _confirmRiskLabel;
        private set => SetProperty(ref _confirmRiskLabel, value);
    }

    public Brush ConfirmRiskForeground
    {
        get => _confirmRiskForeground;
        private set => SetProperty(ref _confirmRiskForeground, value);
    }

    public bool ConfirmShowRisk
    {
        get => _confirmShowRisk;
        private set => SetProperty(ref _confirmShowRisk, value);
    }

    public bool ConfirmRequireAck
    {
        get => _confirmRequireAck;
        private set
        {
            if (SetProperty(ref _confirmRequireAck, value))
            {
                RaiseConfirmState();
            }
        }
    }

    public string ConfirmAckText
    {
        get => _confirmAckText;
        private set => SetProperty(ref _confirmAckText, value);
    }

    public bool ConfirmAcknowledged
    {
        get => _confirmAcknowledged;
        set
        {
            if (SetProperty(ref _confirmAcknowledged, value))
            {
                RaiseConfirmState();
            }
        }
    }

    public bool ConfirmIsBusy
    {
        get => _confirmIsBusy;
        private set
        {
            if (SetProperty(ref _confirmIsBusy, value))
            {
                RaiseConfirmState();
            }
        }
    }

    public string ConfirmButtonText
    {
        get => _confirmButtonText;
        private set => SetProperty(ref _confirmButtonText, value);
    }

    public bool CanConfirm => IsConfirmOpen && !ConfirmIsBusy && (!ConfirmRequireAck || ConfirmAcknowledged);

    // -------------------------------------------------------------- startup

    public bool IsStartupLoading
    {
        get => _isStartupLoading;
        private set
        {
            if (SetProperty(ref _isStartupLoading, value))
            {
                OnPropertyChanged(nameof(ShowStartupEmpty));
            }
        }
    }

    public bool HasStartupError
    {
        get => _hasStartupError;
        private set
        {
            if (SetProperty(ref _hasStartupError, value))
            {
                OnPropertyChanged(nameof(ShowStartupEmpty));
            }
        }
    }

    public string StartupError
    {
        get => _startupError;
        private set => SetProperty(ref _startupError, value);
    }

    public string StartupSummary
    {
        get => _startupSummary;
        private set => SetProperty(ref _startupSummary, value);
    }

    public bool HasStartupApps => StartupApps.Count > 0;

    public bool ShowStartupEmpty => !IsStartupLoading && !HasStartupError && !HasStartupApps;

    // -------------------------------------------------------------- history

    public bool IsHistoryLoading
    {
        get => _isHistoryLoading;
        private set
        {
            if (SetProperty(ref _isHistoryLoading, value))
            {
                OnPropertyChanged(nameof(ShowHistoryEmpty));
            }
        }
    }

    public bool HasHistoryError
    {
        get => _hasHistoryError;
        private set
        {
            if (SetProperty(ref _hasHistoryError, value))
            {
                OnPropertyChanged(nameof(ShowHistoryEmpty));
            }
        }
    }

    public string HistoryError
    {
        get => _historyError;
        private set => SetProperty(ref _historyError, value);
    }

    public string HistorySummary
    {
        get => _historySummary;
        private set => SetProperty(ref _historySummary, value);
    }

    public bool HasHistoryEntries => HistoryEntries.Count > 0;

    public bool ShowHistoryEmpty => !IsHistoryLoading && !HasHistoryError && !HasHistoryEntries;

    public string HistoryEmptyMessage => "No actions yet. Apply an optimization and it will show up here.";

    // -------------------------------------------------------------- lifecycle

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        BuildCards();
        RefreshNavigationCounts();
        await ScanAsync();
        _ = LoadHistoryAsync();
    }

    private void BuildCards()
    {
        Cards.Clear();

        OptimizationAssessment pending = new()
        {
            State = OptimizationState.Unknown,
            Headline = "Waiting for the first scan.",
            Explanation = "The toolkit is inspecting this PC.",
        };

        foreach (IOptimization optimization in _engine.Optimizations
                     .OrderBy(optimization => optimization.Descriptor.Category)
                     .ThenBy(optimization => optimization.Descriptor.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Cards.Add(new CardViewModel(new OptimizationStatus(optimization, pending, null)));
        }

        RefreshView();
    }

    private async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        HasScanError = false;
        ScanError = string.Empty;
        ScanStatus = "Scanning Windows, hardware and the VALORANT client…";
        ScanResult? scanResult = null;

        try
        {
            Task<ScanResult> scanTask = _scanService.ScanAsync();
            var progress = new UiProgress<OptimizationStatus>(ApplyStatus);
            Task<IReadOnlyList<OptimizationStatus>> assessTask = _engine.AssessAllAsync(progress);

            await Task.WhenAll(scanTask, assessTask);
            scanResult = await scanTask;
        }
        catch (Exception ex)
        {
            HasScanError = true;
            ScanError = "Scan failed: " + ex.Message;
        }
        finally
        {
            IsScanning = false;
        }

        if (scanResult is not null)
        {
            BuildSummary(scanResult);
            LastScannedLabel = "Scanned " + DateTime.Now.ToString("HH:mm");
            ScanStatus = string.Empty;
            HasScanError = false;
        }

        RefreshNavigationCounts();
        RefreshView();
        OnPropertyChanged(nameof(AppliedCountLabel));
    }

    private void ApplyStatus(OptimizationStatus status)
    {
        CardViewModel? card = Cards.FirstOrDefault(card => card.Id == status.Optimization.Descriptor.Id);
        if (card is null)
        {
            return;
        }

        card.Update(status);
        OnPropertyChanged(nameof(AppliedCountLabel));
        RefreshSafeRecommendationState();
    }

    /// <summary>Marshals progress synchronously so scan totals are correct as soon as the scan completes.</summary>
    private sealed class UiProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public UiProgress(Action<T> report)
        {
            _report = report;
        }

        public void Report(T value)
        {
            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                _report(value);
            }
            else
            {
                dispatcher.Invoke(() => _report(value));
            }
        }
    }

    private void BuildSummary(ScanResult result)
    {
        SummaryTiles.Clear();
        SystemSnapshot system = result.System;

        SummaryTiles.Add(new SummaryTileViewModel(
            "WINDOWS",
            string.IsNullOrWhiteSpace(system.OsName) ? "Windows" : system.OsName,
            string.Join(" · ", new[] { system.OsDisplayVersion, $"Build {system.OsBuild}" }
                .Where(part => !string.IsNullOrWhiteSpace(part))),
            $"{system.OsName} {system.OsVersion} (build {system.OsBuild})",
            "State.Info"));

        string cpuSubtitle = system.Cpu.PhysicalCores is { } cores
            ? $"{cores} cores · {system.Cpu.LogicalProcessors} threads"
            : $"{system.Cpu.LogicalProcessors} logical processors";
        SummaryTiles.Add(new SummaryTileViewModel(
            "CPU",
            NormalizeCpuName(system.Cpu.Name),
            cpuSubtitle,
            system.Cpu.Name,
            "State.Success"));

        GpuInfo? gpu = system.PrimaryGpu;
        SummaryTiles.Add(new SummaryTileViewModel(
            "GPU",
            NormalizeGpuName(gpu?.Name) ?? "Not detected",
            BuildGpuSubtitle(gpu),
            gpu?.Name ?? "No graphics adapter was detected.",
            "Accent.Default"));

        SummaryTiles.Add(new SummaryTileViewModel(
            "MEMORY",
            FormatBytes(system.TotalMemoryBytes),
            "Installed RAM",
            $"{FormatBytes(system.TotalMemoryBytes)} of system memory",
            "State.Warning"));

        DisplayInfo? display = system.PrimaryDisplay;
        SummaryTiles.Add(new SummaryTileViewModel(
            "DISPLAY",
            display is null ? "Not detected" : $"{display.Width} × {display.Height}",
            display is null ? "No active display reported" : $"{display.RefreshHz} Hz · {display.BitsPerPixel}-bit",
            display is null ? "No active display was reported." : $"{display.Width} × {display.Height} at {display.RefreshHz} Hz",
            "State.Info"));

        ValorantInstallation valorant = result.Valorant;
        SummaryTiles.Add(new SummaryTileViewModel(
            "VALORANT",
            valorant.IsFound ? "Installed" : "Not found",
            valorant.IsFound ? ShortenPath(valorant.InstallDirectory ?? valorant.ExecutablePath ?? string.Empty) : "Client not located",
            valorant.IsFound
                ? valorant.ExecutablePath ?? valorant.DetectionSource ?? "Installed"
                : string.Join(Environment.NewLine, valorant.Notes.Count > 0 ? valorant.Notes : ["VALORANT was not installed or its metadata was not readable."]),
            "Accent.Default"));

        OnPropertyChanged(nameof(HasSummaryTiles));
    }

    // ---------------------------------------------------------------- navigation

    private void BuildNavigation()
    {
        OverviewNav.Add(new NavItemViewModel(
            "optimizations", "Optimizations", IconCatalog.Nav("tweaks"),
            NavPage.Optimizations, null, item => SelectNav(item)));

        ToolNav.Add(new NavItemViewModel(
            "monitoring", "Monitoring", IconCatalog.Nav("monitoring"),
            NavPage.Monitoring, null, item => SelectNav(item)));
        ToolNav.Add(new NavItemViewModel(
            "startup", "Startup apps", IconCatalog.Nav("startup"),
            NavPage.Startup, null, item => SelectNav(item)));
        ToolNav.Add(new NavItemViewModel(
            "history", "Action history", IconCatalog.Nav("history"),
            NavPage.History, null, item => SelectNav(item)));
    }

    private void SelectNav(NavItemViewModel item, bool loadData = true)
    {
        foreach (NavItemViewModel nav in OverviewNav.Concat(CategoryNav).Concat(ToolNav))
        {
            nav.IsSelected = ReferenceEquals(nav, item);
        }

        CurrentPage = item.Page;
        _categoryFilter = item.Categories?.ToList();

        if (item.Page == NavPage.Optimizations)
        {
            PageTitle = "Optimizations";
            PageSubtitle = "Every available tweak in one grid. Unavailable items are greyed out; each toggle asks for confirmation first and can be undone.";
            PageEyebrow = "OPTIMIZATION GRID";
        }
        else if (item.Page == NavPage.Monitoring)
        {
            PageTitle = "Monitoring";
            PageSubtitle = "Live CPU, GPU, memory and NVIDIA sensor telemetry, sampled locally every second. Monitoring is read-only.";
            PageEyebrow = "SYSTEM TELEMETRY";
        }
        else if (item.Page == NavPage.Startup)
        {
            PageTitle = "Startup apps";
            PageSubtitle = "Review what starts with Windows. This screen never changes anything — disabling stays in Task Manager so Windows keeps its own undo path.";
            PageEyebrow = "REVIEW";
        }
        else
        {
            PageTitle = "Action history";
            PageSubtitle = "Every apply, verification and undo is written to a local, human-readable log.";
            PageEyebrow = "LOCAL AUDIT";
        }

        if (item.Page == NavPage.Monitoring)
        {
            Monitoring.Start();
        }
        else
        {
            Monitoring.Stop();
        }

        IsDetailOpen = false;
        IsConfirmOpen = false;
        _pendingConfirmation = null;
        RefreshView();

        if (loadData && item.Page == NavPage.Startup)
        {
            _ = LoadStartupAsync(force: !_startupLoaded);
        }

        if (loadData && item.Page == NavPage.History)
        {
            _ = LoadHistoryAsync();
        }
    }

    private void SelectFilter(FilterChipViewModel chip)
    {
        foreach (FilterChipViewModel filter in StateFilters)
        {
            filter.IsSelected = ReferenceEquals(filter, chip);
        }

        _stateFilter = chip.Key;
        RefreshView();
    }

    private bool FilterCard(object obj)
    {
        if (obj is not CardViewModel card)
        {
            return false;
        }

        if (_categoryFilter is { Count: > 0 } && !_categoryFilter.Contains(card.Category))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string query = SearchText.Trim();
            bool matches =
                card.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                card.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                card.WhatItChanges.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                card.Why.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                card.CategoryLabel.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (!matches)
            {
                return false;
            }
        }

        return _stateFilter switch
        {
            "applied" => card.State == OptimizationState.Applied,
            "available" => card.State == OptimizationState.NotApplied,
            "attention" => card.State is OptimizationState.ReviewRequired or OptimizationState.Unknown,
            "unsupported" => card.State == OptimizationState.Unsupported,
            _ => true,
        };
    }

    private void RefreshView()
    {
        _cardsView.Refresh();
        VisibleCount = _cardsView.Cast<object>().Count();
        OnPropertyChanged(nameof(HasNoVisibleCards));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    /// <summary>
    /// State changes only need a full view refresh when a filter/search is active. Skipping it in
    /// the default view keeps card containers stable (less flicker and a stable accessibility tree).
    /// </summary>
    private void RefreshViewAfterStateChange()
    {
        if (_stateFilter != "all" || !string.IsNullOrWhiteSpace(SearchText))
        {
            RefreshView();
            return;
        }

        OnPropertyChanged(nameof(HasNoVisibleCards));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private void RefreshNavigationCounts()
    {
        OverviewNav[0].CountText = Cards.Count == 0 ? string.Empty : Cards.Count.ToString();

        ToolNav[1].CountText = _startupLoaded && StartupApps.Count > 0 ? StartupApps.Count.ToString() : string.Empty;
        ToolNav[2].CountText = _historyLoaded && HistoryEntries.Count > 0 ? HistoryEntries.Count.ToString() : string.Empty;

        OnPropertyChanged(nameof(AppliedCountLabel));
        RefreshSafeRecommendationState();
    }

    private void RefreshSafeRecommendationState()
    {
        _pendingSafeCards = Cards
            .Where(card => card.State == OptimizationState.NotApplied &&
                           card.Descriptor.Risk == RiskLevel.Safe &&
                           !card.Descriptor.RequiresElevation)
            .ToList();

        OnPropertyChanged(nameof(HasSafeRecommendations));
        OnPropertyChanged(nameof(SafeRecommendationLabel));
        _applySafeCommand.RaiseCanExecuteChanged();
    }

    // -------------------------------------------------------------- user actions

    private void RequestApply(CardViewModel card)
    {
        if (!card.CanApply)
        {
            return;
        }
        _pendingConfirmation = () => ApplyCardAsync(card);
        ConfirmEyebrow = "APPLY OPTIMIZATION";
        ConfirmTitle = card.Name;
        ConfirmMessage = card.WhatItChanges;
        ConfirmRiskLabel = card.RiskLabel;
        ConfirmRiskForeground = card.RiskForeground;
        ConfirmShowRisk = true;
        ConfirmRequireAck = card.Descriptor.Risk != RiskLevel.Safe || card.RequiresElevation;
        ConfirmAckText = card.RequiresElevation
            ? "I understand this changes system-wide settings, and Windows will ask for administrator approval."
            : "I understand this changes Windows settings and that I can undo it later.";
        ConfirmButtonText = "Apply change";
        ConfirmAcknowledged = false;
        ConfirmIsBusy = false;
        IsConfirmOpen = true;
    }

    private void ToggleOptimization(CardViewModel card)
    {
        if (card.IsBusy)
        {
            return;
        }

        switch (card.State)
        {
            case OptimizationState.NotApplied:
                RequestApply(card);
                break;

            case OptimizationState.Applied when card.HasBackup:
                RequestUndo(card);
                break;

            case OptimizationState.Applied:
                Toast.Show(
                    ToastKind.Info,
                    "Nothing to restore yet",
                    "This setting is already in its recommended state. No backup was captured because the toolkit did not change it, so there is nothing to undo.");
                break;

            case OptimizationState.ReviewRequired:
                Toast.Show(
                    ToastKind.Info,
                    "Needs review",
                    string.IsNullOrWhiteSpace(card.BlockedReason)
                        ? "This setting is controlled outside the toolkit."
                        : card.BlockedReason);
                break;

            case OptimizationState.Unsupported:
                Toast.Show(
                    ToastKind.Info,
                    "Not supported on this PC",
                    string.IsNullOrWhiteSpace(card.BlockedReason)
                        ? "The command is not available on this Windows configuration."
                        : card.BlockedReason);
                break;

            default:
                Toast.Show(ToastKind.Info, "State unknown", "Run a rescan before changing this optimization.");
                break;
        }
    }

    private void RequestUndo(CardViewModel card)
    {
        if (!card.CanUndo)
        {
            return;
        }

        _pendingConfirmation = () => UndoCardAsync(card);
        ConfirmEyebrow = "UNDO CHANGE";
        ConfirmTitle = $"Restore “{card.Name}” to its original state?";
        ConfirmMessage = card.HasOriginalValues
            ? "These captured values will be written back exactly:\n\n" +
              string.Join("\n", card.OriginalValues.Take(6)) +
              (card.OriginalValues.Count > 6 ? "\n…" : string.Empty)
            : "The toolkit will restore the captured original settings.";
        ConfirmRiskLabel = card.RiskLabel;
        ConfirmRiskForeground = card.RiskForeground;
        ConfirmShowRisk = false;
        ConfirmRequireAck = false;
        ConfirmAckText = string.Empty;
        ConfirmButtonText = "Restore originals";
        ConfirmAcknowledged = false;
        ConfirmIsBusy = false;
        IsConfirmOpen = true;
    }

    private void RequestApplySafe()
    {
        if (_pendingSafeCards.Count == 0 || _bulkRunning)
        {
            return;
        }

        List<CardViewModel> cards = _pendingSafeCards.ToList();
        _pendingConfirmation = () => ApplySafeAsync(cards);
        ConfirmEyebrow = "APPLY SAFE RECOMMENDATIONS";
        ConfirmTitle = cards.Count == 1 ? cards[0].Name : $"{cards.Count} safe recommendations";
        ConfirmMessage =
            string.Join("\n", cards.Select(card => "• " + card.Name)) +
            "\n\nEach change is captured first, verified after, and can be undone individually.";
        ConfirmRiskLabel = "SAFE";
        ConfirmRiskForeground = UiTheme.Success;
        ConfirmShowRisk = true;
        ConfirmRequireAck = false;
        ConfirmAckText = string.Empty;
        ConfirmButtonText = cards.Count == 1 ? "Apply change" : $"Apply {cards.Count} changes";
        ConfirmAcknowledged = false;
        ConfirmIsBusy = false;
        IsConfirmOpen = true;
    }

    private async Task ConfirmAsync()
    {
        if (!CanConfirm)
        {
            return;
        }

        Func<Task>? action = _pendingConfirmation;
        _pendingConfirmation = null;
        CloseConfirmation();
        if (action is not null)
        {
            await action();
        }
    }

    private void CancelConfirm()
    {
        _pendingConfirmation = null;
        CloseConfirmation();
    }

    private void CloseConfirmation()
    {
        IsConfirmOpen = false;
        ConfirmIsBusy = false;
        RaiseConfirmState();
    }

    private void RaiseConfirmState()
    {
        OnPropertyChanged(nameof(CanConfirm));
        _confirmCommand.RaiseCanExecuteChanged();
    }

    private async Task ApplyCardAsync(CardViewModel card)
    {
        card.SetBusy(true, card.RequiresElevation && !_isElevated
            ? "Waiting for administrator approval…"
            : "Applying change…");
        try
        {
            OperationOutcome outcome = await _engine.ApplyAsync(card.Id);
            OptimizationStatus status = await _engine.AssessAsync(card.Id);
            card.Update(status);
            ReportOutcome(outcome, isUndo: false);
            await LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            Toast.Show(ToastKind.Error, "Apply failed", ex.Message);
        }
        finally
        {
            card.SetBusy(false);
            RefreshNavigationCounts();
            RefreshViewAfterStateChange();
        }
    }

    private async Task UndoCardAsync(CardViewModel card)
    {
        card.SetBusy(true, "Restoring original values…");
        try
        {
            OperationOutcome outcome = await _engine.UndoAsync(card.Id);
            OptimizationStatus status = await _engine.AssessAsync(card.Id);
            card.Update(status);
            ReportOutcome(outcome, isUndo: true);
            await LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            Toast.Show(ToastKind.Error, "Undo failed", ex.Message);
        }
        finally
        {
            card.SetBusy(false);
            RefreshNavigationCounts();
            RefreshViewAfterStateChange();
        }
    }

    private async Task ApplySafeAsync(IReadOnlyList<CardViewModel> cards)
    {
        _bulkRunning = true;
        _applySafeCommand.RaiseCanExecuteChanged();

        int applied = 0;
        var warnings = new List<string>();
        try
        {
            foreach (CardViewModel card in cards)
            {
                if (!card.CanApply)
                {
                    continue;
                }

                card.SetBusy(true, "Applying…");
                try
                {
                    OperationOutcome outcome = await _engine.ApplyAsync(card.Id);
                    OptimizationStatus status = await _engine.AssessAsync(card.Id);
                    card.Update(status);

                    if (outcome.Success)
                    {
                        applied++;
                    }
                    else
                    {
                        warnings.Add($"{card.Name}: {outcome.Message}");
                    }

                    warnings.AddRange(outcome.Warnings.Select(warning => $"{card.Name}: {warning}"));
                }
                catch (Exception ex)
                {
                    warnings.Add($"{card.Name}: {ex.Message}");
                }
                finally
                {
                    card.SetBusy(false);
                }
            }
        }
        finally
        {
            _bulkRunning = false;
            _applySafeCommand.RaiseCanExecuteChanged();
        }

        ToastKind kind = applied == cards.Count ? ToastKind.Success : applied > 0 ? ToastKind.Warning : ToastKind.Error;
        string title = applied == cards.Count
            ? (cards.Count == 1 ? "Safe change applied" : "Safe changes applied")
            : applied > 0
                ? "Some changes were applied"
                : "No changes were applied";
        Toast.Show(kind, title, $"Applied {applied} of {cards.Count} safe recommendations.", warnings);

        await LoadHistoryAsync();
        RefreshNavigationCounts();
        RefreshViewAfterStateChange();
    }

    private void ReportOutcome(OperationOutcome outcome, bool isUndo)
    {
        if (!outcome.Success)
        {
            Toast.Show(
                ToastKind.Error,
                outcome.RolledBack
                    ? "Change failed — original settings restored"
                    : isUndo ? "Undo failed" : "Change failed",
                outcome.Message,
                outcome.Warnings);
            return;
        }

        if (!outcome.Verified)
        {
            Toast.Show(
                ToastKind.Warning,
                isUndo ? "Original settings restored — verify manually" : "Change applied — verify manually",
                outcome.Message,
                outcome.Warnings);
            return;
        }

        Toast.Show(
            ToastKind.Success,
            isUndo ? "Original settings restored" : "Change applied and verified",
            outcome.Message,
            outcome.Warnings);
    }

    private void ShowDetails(CardViewModel card)
    {
        SelectedCard = card;
        IsDetailOpen = true;
    }

    public void DismissTopLayer()
    {
        if (IsConfirmOpen)
        {
            CancelConfirm();
            return;
        }

        if (IsDetailOpen)
        {
            IsDetailOpen = false;
        }
    }

    // ------------------------------------------------------------ data loaders

    private async Task LoadStartupAsync(bool force = false)
    {
        if (IsStartupLoading)
        {
            return;
        }

        if (_startupLoaded && !force)
        {
            return;
        }

        IsStartupLoading = true;
        HasStartupError = false;
        StartupError = string.Empty;

        try
        {
            IReadOnlyList<StartupAppEntry> entries = await _startupScanner.ScanAsync();
            StartupApps.Clear();
            foreach (StartupAppEntry entry in entries
                         .OrderByDescending(entry => entry.IsEnabled)
                         .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                StartupApps.Add(new StartupRowViewModel(entry));
            }

            _startupLoaded = true;
            int enabled = StartupApps.Count(entry => entry.IsEnabled);
            StartupSummary = StartupApps.Count == 0
                ? "No startup applications found"
                : $"{StartupApps.Count} apps · {enabled} enabled";
        }
        catch (Exception ex)
        {
            HasStartupError = true;
            StartupError = "Could not read startup applications: " + ex.Message;
        }
        finally
        {
            IsStartupLoading = false;
            OnPropertyChanged(nameof(HasStartupApps));
            OnPropertyChanged(nameof(ShowStartupEmpty));
            RefreshNavigationCounts();
        }
    }

    private async Task LoadHistoryAsync()
    {
        if (IsHistoryLoading)
        {
            return;
        }

        IsHistoryLoading = true;
        HasHistoryError = false;
        HistoryError = string.Empty;

        try
        {
            IReadOnlyList<ActionLogEntry> entries = await _actionLog.ReadRecentAsync(100);
            HistoryEntries.Clear();
            foreach (ActionLogEntry entry in entries)
            {
                HistoryEntries.Add(new HistoryRowViewModel(entry));
            }

            _historyLoaded = true;
            HistorySummary = entries.Count == 0
                ? "No actions recorded yet"
                : $"{entries.Count} recent action{(entries.Count == 1 ? string.Empty : "s")}";
        }
        catch (Exception ex)
        {
            HasHistoryError = true;
            HistoryError = "Could not read the local action log: " + ex.Message;
        }
        finally
        {
            IsHistoryLoading = false;
            OnPropertyChanged(nameof(HasHistoryEntries));
            OnPropertyChanged(nameof(ShowHistoryEmpty));
            RefreshNavigationCounts();
        }
    }

    private void OpenTaskManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Toast.Show(ToastKind.Error, "Could not open Task Manager", ex.Message);
        }
    }

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.DataRoot);
            Process.Start(new ProcessStartInfo(_paths.DataRoot) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Toast.Show(ToastKind.Error, "Could not open the data folder", ex.Message);
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            Process.Start(new ProcessStartInfo(_paths.LogsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Toast.Show(ToastKind.Error, "Could not open the log folder", ex.Message);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string BuildGpuSubtitle(GpuInfo? gpu)
    {
        if (gpu is null)
        {
            return "No active adapter detected";
        }

        var parts = new List<string>(2);
        if (gpu.MemoryBytes is > 0)
        {
            parts.Add(FormatBytes(gpu.MemoryBytes.Value));
        }

        if (!string.IsNullOrWhiteSpace(gpu.DriverVersion))
        {
            parts.Add(gpu.DriverVersion);
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "Graphics adapter";
    }

    private static string NormalizeCpuName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Unknown CPU";
        }

        return name
            .Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" CPU", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();
    }

    private static string? NormalizeGpuName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string normalized = name
            .Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (normalized.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["NVIDIA ".Length..];
        }

        return normalized;
    }

    private static string FormatBytes(ulong bytes)
    {
        const double gigabyte = 1024d * 1024d * 1024d;
        const double megabyte = 1024d * 1024d;
        if (bytes >= gigabyte)
        {
            double gb = bytes / gigabyte;
            return gb >= 10 ? $"{gb:0} GB" : $"{gb:0.0} GB";
        }

        if (bytes >= megabyte)
        {
            return $"{bytes / megabyte:0} MB";
        }

        return $"{bytes} B";
    }

    private static string ShortenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Not found";
        }

        string trimmed = path.TrimEnd('\\');
        if (trimmed.Length <= 40)
        {
            return trimmed;
        }

        return trimmed[..18] + "…" + trimmed[^20..];
    }
}
