using System.Globalization;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Reads and rewrites the REG_SZ payload used by
/// <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c>. Windows stores one value per
/// executable whose name is the full executable path and whose data is a semicolon-separated
/// list of <c>Key=Value</c> pairs, normally with a trailing semicolon, for example
/// <c>GpuPreference=2;SwapEffectUpgradeEnable=1;</c>.
/// </summary>
internal static class UserGpuPreferencesData
{
    internal const string PreferenceName = "GpuPreference";
    internal const int HighPerformance = 2;
    internal const string HighPerformanceData = "GpuPreference=2;";

    /// <summary>Returns the stored <c>GpuPreference</c> value, or null when it is absent/invalid.</summary>
    public static int? TryGetPreference(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        foreach (string segment in data.Split(';'))
        {
            if (!TryParsePair(segment, out string key, out string value) ||
                !key.Equals(PreferenceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : null;
        }

        return null;
    }

    /// <summary>
    /// Returns a copy of <paramref name="existing"/> with <c>GpuPreference=2</c> replaced or
    /// inserted. Unrelated pairs keep their order, spacing and casing; the serialized result keeps
    /// the format's customary trailing semicolon.
    /// </summary>
    public static string Merge(string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return HighPerformanceData;
        }

        string[] segments = existing.Split(';');
        bool replaced = false;

        for (int i = 0; i < segments.Length; i++)
        {
            if (!TryParsePair(segments[i], out string key, out _) ||
                !key.Equals(PreferenceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Keep whatever key spelling/spacing the user already had and only replace the value.
            int equalsIndex = segments[i].IndexOf('=');
            segments[i] = segments[i][..equalsIndex] + "=2";
            replaced = true;
            break;
        }

        if (!replaced)
        {
            // Insert before the empty segment produced by a trailing ';' so the convention and
            // any unusual extra separators already present are retained.
            if (segments.Length > 0 && segments[^1].Length == 0)
            {
                segments[^1] = "GpuPreference=2";
            }
            else
            {
                segments = [.. segments, "GpuPreference=2"];
            }
        }

        string merged = string.Join(';', segments);
        return merged.EndsWith(';') ? merged : merged + ";";
    }

    private static bool TryParsePair(string segment, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        int equalsIndex = segment.IndexOf('=');
        if (equalsIndex <= 0)
        {
            return false;
        }

        key = segment[..equalsIndex].Trim();
        value = segment[(equalsIndex + 1)..].Trim();
        return key.Length > 0;
    }
}
