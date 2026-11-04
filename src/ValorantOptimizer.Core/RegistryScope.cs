using Microsoft.Win32;

namespace ValorantOptimizer.Core;

/// <summary>
/// Declares a registry location an optimization is allowed to modify. Undo data is validated
/// against these scopes before the (potentially elevated) restore path trusts it.
/// </summary>
public sealed record RegistryScope(RegistryHive Hive, string SubKey)
{
    public bool Contains(RegistryHive hive, string subKey)
    {
        if (hive != Hive)
        {
            return false;
        }

        return string.Equals(SubKey, subKey, StringComparison.OrdinalIgnoreCase) ||
               subKey.StartsWith(SubKey + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => $"{Hive}\\{SubKey}";
}
