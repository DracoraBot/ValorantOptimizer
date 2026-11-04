using Microsoft.Win32;

namespace ValorantOptimizer.Core;

/// <summary>
/// Real registry access. Opens keys read-only for reads and writable only for writes/deletes.
/// This class does not request elevation; callers decide whether an operation may run in the
/// current process or must be routed through the elevated helper.
/// </summary>
public sealed class RegistryStore : IRegistryStore
{
    public RegistryValue? ReadValue(RegistryHive hive, string subKey, string valueName)
    {
        using RegistryKey? key = OpenBaseKey(hive).OpenSubKey(subKey, writable: false);
        if (key is null)
        {
            return null;
        }

        if (!ValueNameExists(key, valueName))
        {
            return null;
        }

        RegistryValueKind kind = key.GetValueKind(valueName);
        object? raw = key.GetValue(valueName, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        object normalized = raw ?? kind switch
        {
            RegistryValueKind.Binary => Array.Empty<byte>(),
            RegistryValueKind.MultiString => Array.Empty<string>(),
            _ => string.Empty,
        };
        return new RegistryValue(hive, subKey, valueName, kind, normalized);
    }

    public bool ValueExists(RegistryHive hive, string subKey, string valueName)
    {
        using RegistryKey? key = OpenBaseKey(hive).OpenSubKey(subKey, writable: false);
        return key is not null && ValueNameExists(key, valueName);
    }

    public IReadOnlyList<string> GetValueNames(RegistryHive hive, string subKey)
    {
        using RegistryKey? key = OpenBaseKey(hive).OpenSubKey(subKey, writable: false);
        return key?.GetValueNames() ?? [];
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string subKey)
    {
        using RegistryKey? key = OpenBaseKey(hive).OpenSubKey(subKey, writable: false);
        return key?.GetSubKeyNames() ?? [];
    }

    public bool KeyExists(RegistryHive hive, string subKey)
    {
        using RegistryKey? key = OpenBaseKey(hive).OpenSubKey(subKey, writable: false);
        return key is not null;
    }

    public void WriteValue(RegistryHive hive, string subKey, string valueName, RegistryValueKind kind, object data)
    {
        using RegistryKey baseKey = OpenBaseKey(hive);
        using RegistryKey key = baseKey.CreateSubKey(subKey, writable: true)
            ?? throw new InvalidOperationException($@"Could not open or create registry key {hive}\{subKey}.");
        key.SetValue(valueName, data, kind);
    }

    public void DeleteValue(RegistryHive hive, string subKey, string valueName)
    {
        using RegistryKey baseKey = OpenBaseKey(hive);
        using RegistryKey? key = baseKey.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private static RegistryKey OpenBaseKey(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Default);

    private static bool ValueNameExists(RegistryKey key, string valueName) =>
        key.GetValueNames().Any(name => string.Equals(name, valueName, StringComparison.OrdinalIgnoreCase));
}
