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
