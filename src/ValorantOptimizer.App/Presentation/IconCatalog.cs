using System.Windows.Media;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.App.Presentation;

/// <summary>
/// Hand-authored 24x24 stroke icons. Vector paths are used instead of an icon font so every
/// category renders identically on Windows 10 and 11 without missing-glyph boxes.
/// </summary>
public static class IconCatalog
{
    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.Ordinal);

    public static Geometry Nav(string key) => key switch
    {
        "overview" => Parse("M3,11 L12,3 L21,11 M6,9 V20 H18 V9"),
        "tweaks" => Tweak,
        "monitoring" => Monitoring,
        "mouse" => Mouse,
        "windows" => Windows,
        "cpu" => Cpu,
        "gpu" => Gpu,
        "startup" => Startup,
        "history" => History,
        _ => Parse("M4,4 H20 V20 H4 Z"),
    };

    public static Geometry Category(OptimizationCategory category) => category switch
    {
        OptimizationCategory.Mouse => Mouse,
        OptimizationCategory.Windows => Windows,
        OptimizationCategory.CpuPower => Cpu,
        OptimizationCategory.Gpu => Gpu,
        OptimizationCategory.Memory => Memory,
        OptimizationCategory.Network => Network,
        OptimizationCategory.Usb => Usb,
        OptimizationCategory.Storage => Storage,
        OptimizationCategory.Valorant => Crosshair,
        OptimizationCategory.Startup => Startup,
        _ => Parse("M4,4 H20 V20 H4 Z"),
    };

    public static Geometry Mouse { get; } = Parse(
        "M8,2 H16 A6,6 0 0 1 22,8 V16 A6,6 0 0 1 16,22 H8 A6,6 0 0 1 2,16 V8 A6,6 0 0 1 8,2 Z M12,3 V9");

    public static Geometry Windows { get; } = Parse(
        "M3,3 H11 V11 H3 Z M13,3 H21 V11 H13 Z M3,13 H11 V21 H3 Z M13,13 H21 V21 H13 Z");

    public static Geometry Cpu { get; } = Parse(
        "M7,7 H17 V17 H7 Z M10,3 V7 M14,3 V7 M10,17 V21 M14,17 V21 M3,10 H7 M3,14 H7 M17,10 H21 M17,14 H21");

    public static Geometry Gpu { get; } = Parse(
        "M2,7 H22 V17 H2 Z M5,10 H12 V14 H5 Z M16,10 H19 M16,13 H19");

    public static Geometry Crosshair { get; } = Parse(
        "M12,2 V7 M12,17 V22 M2,12 H7 M17,12 H22 M6,6 L9,9 M18,6 L15,9 M6,18 L9,15 M18,18 L15,15");

    public static Geometry Memory { get; } = Parse(
        "M3,8 H21 V16 H3 Z M6,8 V11 M9,8 V11 M12,8 V11 M15,8 V11 M18,8 V11 M6,16 V19 M18,16 V19");

    public static Geometry Network { get; } = Parse(
        "M2,9 C6,5 18,5 22,9 M5,13 C8,10.5 16,10.5 19,13 M9,17 C10.5,15.7 13.5,15.7 15,17 M12,20 L12.01,20");

    public static Geometry Usb { get; } = Parse(
        "M12,3 V18 M12,3 L9,6 M12,3 L15,6 M12,9 L8,13 V16 M12,9 L16,13 V16 M12,18 A2,2 0 1 0 12,22 A2,2 0 1 0 12,18");

    public static Geometry Storage { get; } = Parse(
        "M3,5 H21 V10 H3 Z M3,14 H21 V19 H3 Z M17,7.5 H18 M17,16.5 H18");

    public static Geometry Tweak { get; } = Parse(
        "M13,2 L4,14 H11 L10,22 L20,10 H13 Z");

    public static Geometry Monitoring { get; } = Parse(
        "M2,12 H6 L9,5 L13,19 L16,12 H22");

    public static Geometry Startup { get; } = Parse(
        "M12,3 L19,10 H15 V19 H9 V10 H5 Z");

    public static Geometry History { get; } = Parse(
        "M12,3 A9,9 0 1 1 12,21 A9,9 0 1 1 12,3 Z M12,7 V12 L15,14");

    public static Geometry ChevronRight { get; } = Parse("M9,5 L16,12 L9,19");

    public static Geometry Folder { get; } = Parse("M3,6 H10 L12,9 H21 V19 H3 Z");

    private static Geometry Parse(string data)
    {
        if (Cache.TryGetValue(data, out Geometry? cached))
        {
            return cached;
        }

        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        Cache[data] = geometry;
        return geometry;
    }
}
