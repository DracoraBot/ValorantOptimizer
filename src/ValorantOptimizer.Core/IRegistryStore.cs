using Microsoft.Win32;

namespace ValorantOptimizer.Core;

/// <summary>A registry value read from the machine.</summary>
public sealed record RegistryValue(
    RegistryHive Hive,
    string SubKey,
    string ValueName,
    RegistryValueKind Kind,
    object Data)
{
    public string DisplayValue => RegistryValueCodec.Serialize(Data, Kind);
}

/// <summary>
/// Thin, testable abstraction over the Windows registry. Production code uses
/// <see cref="RegistryStore"/>; tests use an in-memory fake.
/// </summary>
public interface IRegistryStore
{
    /// <summary>Reads a value, or returns null when the key or value does not exist.</summary>
    RegistryValue? ReadValue(RegistryHive hive, string subKey, string valueName);

    bool ValueExists(RegistryHive hive, string subKey, string valueName);

    IReadOnlyList<string> GetValueNames(RegistryHive hive, string subKey);

    IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string subKey);

    bool KeyExists(RegistryHive hive, string subKey);

    /// <summary>Creates the key (and parents) when needed, then writes the value.</summary>
    void WriteValue(RegistryHive hive, string subKey, string valueName, RegistryValueKind kind, object data);

    /// <summary>Deletes the value when present; does nothing when missing.</summary>
    void DeleteValue(RegistryHive hive, string subKey, string valueName);
}
