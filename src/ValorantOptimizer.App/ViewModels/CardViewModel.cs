using System.Windows.Media;
using ValorantOptimizer.App.Infrastructure;
using ValorantOptimizer.App.Presentation;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.App.ViewModels;

/// <summary>One optimization card. Wraps the engine status and exposes display-ready values.</summary>
public sealed class CardViewModel : ObservableObject
{
    private OptimizationStatus _status;
    private bool _isBusy;
    private string _busyText = "Working…";

    public CardViewModel(OptimizationStatus status)
    {
        _status = status ?? throw new ArgumentNullException(nameof(status));
    }

    public OptimizationStatus Status => _status;

    public IOptimization Optimization => _status.Optimization;

    public OptimizationDescriptor Descriptor => Optimization.Descriptor;

    public string Id => Descriptor.Id;

    public string Name => Descriptor.Name;

    public string Summary => Descriptor.Summary;

    public string WhatItChanges => Descriptor.WhatItChanges;

    public string Why => Descriptor.Why;

    public string Compatibility => string.IsNullOrWhiteSpace(Descriptor.Compatibility)
        ? "No special hardware requirements."
        : Descriptor.Compatibility!;

    public IReadOnlyList<string> Notes => Descriptor.Notes;

    public bool HasNotes => Descriptor.Notes.Count > 0;

    public OptimizationCategory Category => Descriptor.Category;

    public CategoryPresentation CategoryInfo => PresentationCatalog.For(Descriptor.Category);

    public string CategoryLabel => CategoryInfo.Label;

    public Geometry CategoryIcon => CategoryInfo.Icon;

    public Brush CategoryAccent => CategoryInfo.Accent;

    public Brush CategorySoft => CategoryInfo.Soft;

    public string RiskLabel => PresentationCatalog.RiskLabel(Descriptor.Risk);

    public Brush RiskForeground => PresentationCatalog.RiskForeground(Descriptor.Risk);

    public string RestartLabel => PresentationCatalog.RestartLabel(Descriptor.Restart);

    public bool HasRestart => !string.IsNullOrEmpty(RestartLabel);

    public bool RequiresElevation => Descriptor.RequiresElevation;

    public bool ShowMetaRow => HasRestart || RequiresElevation;

    public IReadOnlyList<string> ManagedLocations =>
        Descriptor.ManagedScopes.Select(scope => scope.ToString()).ToList();

    public bool HasManagedLocations => Descriptor.ManagedScopes.Count > 0;

    public string MetaLabel
    {
        get
        {
            var parts = new List<string>(2);
            if (HasRestart)
            {
                parts.Add(RestartLabel);
            }

            if (RequiresElevation)
            {
                parts.Add("Admin approval");
            }

            return string.Join(" · ", parts);
        }
    }

    // ---------------------------------------------------------------- state

    public OptimizationAssessment Assessment => _status.Assessment;

    public OptimizationState State => Assessment.State;

    public string StateLabel => State switch
    {
        OptimizationState.Applied => Descriptor.StateWhenApplied,
        OptimizationState.NotApplied => "Available",
        OptimizationState.Unsupported => "Unsupported",
        OptimizationState.ReviewRequired => "Needs review",
        _ => "Unknown",
    };

    public string Headline => Assessment.Headline;

    public string Explanation => Assessment.Explanation ?? string.Empty;

    public bool HasExplanation => !string.IsNullOrWhiteSpace(Explanation);

    public IReadOnlyList<AssessmentEvidence> Evidence => Assessment.Evidence;

    public bool HasEvidence => Evidence.Count > 0;

    public string BlockedReason => Assessment.BlockedReason ?? string.Empty;

    public bool HasBlockedReason => !string.IsNullOrWhiteSpace(BlockedReason);

    public Brush StateForeground => State switch
    {
        OptimizationState.Applied => UiTheme.Success,
        OptimizationState.NotApplied => UiTheme.Info,
        OptimizationState.ReviewRequired => UiTheme.Warning,
        OptimizationState.Unsupported => UiTheme.Neutral,
        _ => UiTheme.Neutral,
    };

    public Brush StateSoft => State switch
    {
        OptimizationState.Applied => UiTheme.SuccessSoft,
        OptimizationState.NotApplied => UiTheme.InfoSoft,
        OptimizationState.ReviewRequired => UiTheme.WarningSoft,
        OptimizationState.Unsupported => UiTheme.NeutralSoft,
        _ => UiTheme.NeutralSoft,
    };

    public Brush StateBorder => State switch
    {
        OptimizationState.Applied => UiTheme.Brush("State.SuccessBorder"),
        OptimizationState.ReviewRequired => UiTheme.Brush("State.WarningBorder"),
        OptimizationState.NotApplied => UiTheme.Brush("Border.Default"),
        _ => UiTheme.Brush("Border.Default"),
    };

    // -------------------------------------------------------------- backups

    public bool HasBackup => _status.Backup is not null;

    public ChangeSet? Backup => _status.Backup;

    public string BackupLabel => HasBackup
        ? $"Original captured {_status.Backup!.CapturedAt.LocalDateTime:MMM d, HH:mm}"
        : "No restore point captured yet";

    public IReadOnlyList<string> OriginalValues =>
        _status.Backup?.Entries.Select(entry => entry.ToString()).ToList() ?? [];

    public bool HasOriginalValues => OriginalValues.Count > 0;

    public string OriginalStateNote => !HasBackup
        ? "No backup yet. The toolkit captures every original value automatically before applying a change, so Undo is always available afterwards."
        : !HasOriginalValues
            ? "This optimization records its restore data as metadata rather than registry values."
            : string.Empty;

    public bool HasOriginalStateNote => OriginalStateNote.Length > 0;

    public string BackupSourceLabel => _status.Backup is null
        ? string.Empty
        : $"Schema v{_status.Backup.SchemaVersion} · {_status.Backup.Entries.Count} value(s)";

    // --------------------------------------------------------------- actions

    public bool IsBusy => _isBusy;

    public string BusyText => _busyText;

    public bool CanApply => State == OptimizationState.NotApplied && !IsBusy;

    public bool CanUndo => State == OptimizationState.Applied && HasBackup && !IsBusy;

    public bool ShowApply => State == OptimizationState.NotApplied;

    public bool ShowUndo => State == OptimizationState.Applied && HasBackup;

    public bool ShowUnavailable => State is OptimizationState.Unsupported or OptimizationState.ReviewRequired or OptimizationState.Unknown;

    public string UnavailableLabel => State switch
    {
        OptimizationState.ReviewRequired => "Needs review",
        OptimizationState.Unsupported => "Unsupported",
        _ => "Unavailable",
    };

    public bool ShowIdleActions => !IsBusy;

    public bool ShowBusy => IsBusy;

    public void Update(OptimizationStatus status)
    {
        _status = status ?? throw new ArgumentNullException(nameof(status));
        RaiseAllPropertiesChanged();
    }

    public void SetBusy(bool isBusy, string? text = null)
    {
        _isBusy = isBusy;
        if (!string.IsNullOrWhiteSpace(text))
        {
            _busyText = text;
        }

        RaiseAllPropertiesChanged();
    }
}
