using System.Windows;
using System.Windows.Media;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.App.Presentation;

/// <summary>Resolves theme brushes once, so view models can expose colors without XAML converters.</summary>
public static class UiTheme
{
    private static readonly Dictionary<string, Brush> Cache = new(StringComparer.Ordinal);

    public static Brush Brush(string key)
    {
        if (Cache.TryGetValue(key, out Brush? cached))
        {
            return cached;
        }

        Brush brush;
        if (Application.Current?.TryFindResource(key) is Brush found)
        {
            brush = found;
        }
        else
        {
            brush = Brushes.Transparent;
        }

        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        Cache[key] = brush;
        return brush;
    }

    public static Brush Accent => Brush("Accent.Default");

    public static Brush Success => Brush("State.Success");

    public static Brush SuccessSoft => Brush("State.SuccessSoft");

    public static Brush Warning => Brush("State.Warning");

    public static Brush WarningSoft => Brush("State.WarningSoft");

    public static Brush Danger => Brush("State.Danger");

    public static Brush DangerSoft => Brush("State.DangerSoft");

    public static Brush Info => Brush("State.Info");

    public static Brush InfoSoft => Brush("State.InfoSoft");

    public static Brush Neutral => Brush("State.Neutral");

    public static Brush NeutralSoft => Brush("State.NeutralSoft");

    public static Brush TextPrimary => Brush("Text.Primary");

    public static Brush TextSecondary => Brush("Text.Secondary");

    public static Brush TextMuted => Brush("Text.Muted");

    public static Brush White => Brush("Text.White");

    public static Brush Transparent => Brush("Transparent");
}

public sealed record CategoryPresentation(
    string Label,
    Geometry Icon,
    string AccentKey,
    string SoftKey,
    string Description)
{
    public Brush Accent => UiTheme.Brush(AccentKey);

    public Brush Soft => UiTheme.Brush(SoftKey);
}

public static class PresentationCatalog
{
    public static CategoryPresentation For(OptimizationCategory category) => category switch
    {
        OptimizationCategory.Mouse => new CategoryPresentation(
            "Mouse", IconCatalog.Category(OptimizationCategory.Mouse), "State.Info", "State.InfoSoft",
            "Pointer behavior, acceleration and precision settings."),
        OptimizationCategory.Windows => new CategoryPresentation(
            "Windows", IconCatalog.Category(OptimizationCategory.Windows), "State.Success", "State.SuccessSoft",
            "Game Mode, background capture and download behavior."),
        OptimizationCategory.CpuPower => new CategoryPresentation(
            "CPU & power", IconCatalog.Category(OptimizationCategory.CpuPower), "State.Warning", "State.WarningSoft",
            "Processor power behavior and plan selection."),
        OptimizationCategory.Gpu => new CategoryPresentation(
            "GPU", IconCatalog.Category(OptimizationCategory.Gpu), "Accent.Default", "Accent.Soft",
            "Per-application graphics preferences."),
        OptimizationCategory.Memory => new CategoryPresentation(
            "Memory", IconCatalog.Category(OptimizationCategory.Memory), "Category.Memory", "Category.Memory.Soft",
            "Memory configuration and background usage."),
        OptimizationCategory.Network => new CategoryPresentation(
            "Network", IconCatalog.Category(OptimizationCategory.Network), "State.Info", "State.InfoSoft",
            "Adapter power, DNS and network diagnostics."),
        OptimizationCategory.Usb => new CategoryPresentation(
            "USB", IconCatalog.Category(OptimizationCategory.Usb), "State.Success", "State.SuccessSoft",
            "Selective suspend and device power management."),
        OptimizationCategory.Storage => new CategoryPresentation(
            "Storage", IconCatalog.Category(OptimizationCategory.Storage), "State.Warning", "State.WarningSoft",
            "Temporary files, storage health and background disk activity."),
        OptimizationCategory.Valorant => new CategoryPresentation(
            "VALORANT", IconCatalog.Category(OptimizationCategory.Valorant), "Accent.Default", "Accent.Soft",
            "Settings applied to the installed VALORANT client."),
        OptimizationCategory.Startup => new CategoryPresentation(
            "Startup", IconCatalog.Category(OptimizationCategory.Startup), "State.Neutral", "State.NeutralSoft",
            "Applications that start with Windows."),
        _ => new CategoryPresentation("Other", IconCatalog.Nav("overview"), "State.Neutral", "State.NeutralSoft", string.Empty),
    };

    public static string RiskLabel(RiskLevel risk) => risk switch
    {
        RiskLevel.Safe => "SAFE",
        RiskLevel.Advanced => "ADVANCED",
        RiskLevel.Experimental => "EXPERIMENTAL",
        _ => "UNKNOWN",
    };

    public static Brush RiskForeground(RiskLevel risk) => risk switch
    {
        RiskLevel.Safe => UiTheme.Success,
        RiskLevel.Advanced => UiTheme.Warning,
        RiskLevel.Experimental => UiTheme.Danger,
        _ => UiTheme.TextMuted,
    };

    public static string RestartLabel(RestartRequirement restart) => restart switch
    {
        RestartRequirement.SignOut => "Sign out required",
        RestartRequirement.Reboot => "Restart required",
        _ => string.Empty,
    };

    public static string CategoryTitle(IReadOnlyList<OptimizationCategory>? categories) =>
        categories is { Count: > 0 }
            ? string.Join(" · ", categories.Select(category => For(category).Label))
            : "Overview";

    public static string CategoryDescription(IReadOnlyList<OptimizationCategory>? categories)
    {
        if (categories is null || categories.Count == 0)
        {
            return "Hardware-aware Windows configuration for VALORANT. Every change is captured first, verified after, and reversible with one click.";
        }

        return categories.Count == 1
            ? For(categories[0]).Description
            : "Per-title graphics preferences and the power behavior they run under.";
    }
}
