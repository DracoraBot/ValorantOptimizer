using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ValorantOptimizer.App.Infrastructure;
using ValorantOptimizer.App.Presentation;

namespace ValorantOptimizer.App.ViewModels;

public enum ToastKind
{
    Success,
    Warning,
    Error,
    Info,
}

/// <summary>Bottom-right, auto-dismissing result notification.</summary>
public sealed class ToastViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private bool _isVisible;
    private string _title = string.Empty;
    private string _message = string.Empty;
    private string _warnings = string.Empty;
    private bool _hasWarnings;
    private string _glyph = "\uE73E";
    private Brush _accent = UiTheme.Success;
    private Brush _soft = UiTheme.SuccessSoft;

    public ToastViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _timer.Tick += (_, _) => Dismiss();
        DismissCommand = new RelayCommand(Dismiss);
    }

    public ICommand DismissCommand { get; }

    public bool IsVisible
    {
        get => _isVisible;
        private set => SetProperty(ref _isVisible, value);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public string Warnings
    {
        get => _warnings;
        private set
        {
            if (SetProperty(ref _warnings, value))
            {
                OnPropertyChanged(nameof(HasWarnings));
            }
        }
    }

    public bool HasWarnings
    {
        get => _hasWarnings;
        private set => SetProperty(ref _hasWarnings, value);
    }

    public string Glyph
    {
        get => _glyph;
        private set => SetProperty(ref _glyph, value);
    }

    public Brush Accent
    {
        get => _accent;
        private set => SetProperty(ref _accent, value);
    }

    public Brush Soft
    {
        get => _soft;
        private set => SetProperty(ref _soft, value);
    }

    public void Show(ToastKind kind, string title, string message, IEnumerable<string>? warnings = null)
    {
        Title = title;
        Message = message;
        List<string> warningList = warnings?.Where(w => !string.IsNullOrWhiteSpace(w)).ToList() ?? [];
        Warnings = string.Join(Environment.NewLine, warningList);
        HasWarnings = warningList.Count > 0;

        (Glyph, Accent, Soft) = kind switch
        {
            ToastKind.Success => ("\uE73E", UiTheme.Success, UiTheme.SuccessSoft),
            ToastKind.Warning => ("\uE7BA", UiTheme.Warning, UiTheme.WarningSoft),
            ToastKind.Error => ("\uEA39", UiTheme.Danger, UiTheme.DangerSoft),
            _ => ("\uE946", UiTheme.Info, UiTheme.InfoSoft),
        };

        IsVisible = true;
        _timer.Stop();
        _timer.Start();
    }

    public void Dismiss()
    {
        _timer.Stop();
        IsVisible = false;
    }
}
