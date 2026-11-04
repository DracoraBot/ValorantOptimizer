using System.Windows.Input;
using System.Windows.Media;
using ValorantOptimizer.App.Infrastructure;
using ValorantOptimizer.App.Presentation;
using ValorantOptimizer.Core;
using ValorantOptimizer.Diagnostics;

namespace ValorantOptimizer.App.ViewModels;

public enum NavPage
{
    Optimizations,
    Monitoring,
    Startup,
    History,
}

public sealed class NavItemViewModel : ObservableObject
{
    private bool _isSelected;
    private string _countText = string.Empty;

    public NavItemViewModel(
        string key,
        string title,
        Geometry icon,
        NavPage page,
        IReadOnlyList<OptimizationCategory>? categories,
        Action<NavItemViewModel> select)
    {
        Key = key;
        Title = title;
        Icon = icon;
        Page = page;
        Categories = categories;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public string Key { get; }

    public string Title { get; }

    public Geometry Icon { get; }

    public NavPage Page { get; }

    public IReadOnlyList<OptimizationCategory>? Categories { get; }

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string CountText
    {
        get => _countText;
        set
        {
            if (SetProperty(ref _countText, value))
            {
                OnPropertyChanged(nameof(HasCount));
            }
        }
    }

    public bool HasCount => !string.IsNullOrEmpty(CountText);
}

public sealed class FilterChipViewModel : ObservableObject
{
    private bool _isSelected;

    public FilterChipViewModel(string key, string label, Action<FilterChipViewModel> select)
    {
        Key = key;
        Label = label;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public string Key { get; }

    public string Label { get; }

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class SummaryTileViewModel
{
    public SummaryTileViewModel(string label, string value, string subValue, string tooltip, string accentKey)
    {
        Label = label;
        Value = value;
        SubValue = subValue;
        Tooltip = tooltip;
        Accent = UiTheme.Brush(accentKey);
    }

    public string Label { get; }

    public string Value { get; }

    public string SubValue { get; }

    public string Tooltip { get; }

    public Brush Accent { get; }
}

public sealed class StartupRowViewModel
{
    public StartupRowViewModel(StartupAppEntry entry)
    {
        Entry = entry;
        Initial = string.IsNullOrWhiteSpace(entry.Name)
            ? "?"
            : entry.Name.Trim()[..1].ToUpperInvariant();
    }

    public StartupAppEntry Entry { get; }

    public string Name => Entry.Name;

    public string Command => Entry.Command;

    public string Publisher => string.IsNullOrWhiteSpace(Entry.Publisher) ? "Unknown publisher" : Entry.Publisher!;

    public string SourceLabel => Entry.Source switch
    {
        StartupSource.UserRunKey => "HKCU Run",
        StartupSource.MachineRunKey => "HKLM Run",
        StartupSource.UserStartupFolder => "User Startup",
        StartupSource.MachineStartupFolder => "All users Startup",
        StartupSource.ScheduledTask => "Scheduled task",
        _ => "Unknown",
    };

    public bool IsEnabled => Entry.IsEnabled;

    public string EnabledLabel => IsEnabled ? "Enabled" : "Disabled";

    public Brush EnabledForeground => IsEnabled ? UiTheme.Success : UiTheme.TextMuted;

    public Brush EnabledSoft => IsEnabled ? UiTheme.SuccessSoft : UiTheme.NeutralSoft;

    public string FlagsLabel => string.Join("  ·  ", Entry.Flags);

    public bool HasFlags => Entry.Flags.Count > 0;

    public string Note => Entry.Note ?? string.Empty;

    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    public string Initial { get; }

    public Brush AvatarBackground => HasFlags ? UiTheme.WarningSoft : UiTheme.NeutralSoft;

    public Brush AvatarForeground => HasFlags ? UiTheme.Warning : UiTheme.TextSecondary;
}

public sealed class HistoryRowViewModel
{
    public HistoryRowViewModel(ActionLogEntry entry)
    {
        Entry = entry;
    }

    public ActionLogEntry Entry { get; }

    public string TimeLabel => Entry.Timestamp.LocalDateTime.ToString("MMM d · HH:mm");

    public string OptimizationName => Entry.OptimizationName;

    public string Message => Entry.Message ?? string.Empty;

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public string ActionLabel => Entry.Action == "apply" ? "Applied" : "Restored";

    public string ResultLabel => Entry.Result switch
    {
        "verified" => "Verified",
        "applied-unverified" => "Applied · unverified",
        "restored-unverified" => "Restored · unverified",
        "rolled-back" => "Rolled back",
        "failed" => "Failed",
        _ => Entry.Result,
    };

    public bool IsFailure => Entry.Result is "failed" or "rolled-back";

    public bool IsUnverified => Entry.Result is "applied-unverified" or "restored-unverified";

    public Brush Accent => IsFailure ? UiTheme.Danger : IsUnverified ? UiTheme.Warning : UiTheme.Success;

    public Brush Soft => IsFailure ? UiTheme.DangerSoft : IsUnverified ? UiTheme.WarningSoft : UiTheme.SuccessSoft;

    public string Glyph => Entry.Action == "apply" ? "\uE73E" : "\uE7A7";

    public string ChangesText => string.Join(Environment.NewLine, Entry.Changes);

    public bool HasChanges => Entry.Changes.Count > 0;
}
