namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Reads and merges the REG_SZ token list used by
/// <c>HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers</c>. Windows stores
/// compatibility shims for one executable as a whitespace-separated token list whose first token
/// is normally the <c>~</c> marker, for example <c>~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE</c>.
/// </summary>
internal static class AppCompatLayerData
{
    internal const string Flag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

    internal const string DefaultData = "~ " + Flag;

    /// <summary>True when the flag appears as a whole token (with or without a leading '~').</summary>
    public static bool ContainsFlag(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return false;
        }

        foreach (string token in data.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsFlagToken(token))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns a copy of <paramref name="existing"/> with the flag appended. Unrelated tokens keep
    /// their order, casing and internal spacing; the <c>~</c> marker is added only when absent.
    /// </summary>
    public static string Merge(string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return DefaultData;
        }

        if (ContainsFlag(existing))
        {
            return existing;
        }

        string trimmed = existing.Trim();
        return trimmed.StartsWith("~", StringComparison.Ordinal)
            ? $"{trimmed} {Flag}"
            : $"~ {trimmed} {Flag}";
    }

    private static bool IsFlagToken(string token) =>
        token.TrimStart('~').Equals(Flag, StringComparison.OrdinalIgnoreCase);
}
