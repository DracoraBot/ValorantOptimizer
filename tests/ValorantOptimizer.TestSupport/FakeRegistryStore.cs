using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.TestSupport;

/// <summary>In-memory <see cref="IRegistryStore"/> used by every test project.</summary>
public sealed class FakeRegistryStore : IRegistryStore
{
    private readonly Dictionary<string, RegistryValue> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public int WriteCount { get; private set; }

    public int DeleteCount { get; private set; }

    public List<string> Operations { get; } = [];

    public RegistryValue? ReadValue(RegistryHive hive, string subKey, string valueName)
    {
        lock (_sync)
        {
            return _values.TryGetValue(Key(hive, subKey, valueName), out RegistryValue? value) ? value : null;
        }
    }

    public bool ValueExists(RegistryHive hive, string subKey, string valueName) => ReadValue(hive, subKey, valueName) is not null;

    public IReadOnlyList<string> GetValueNames(RegistryHive hive, string subKey)
    {
        lock (_sync)
        {
            return _values.Values
                .Where(value => value.Hive == hive && PathEquals(value.SubKey, subKey))
                .Select(value => value.ValueName)
                .ToList();
        }
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, string subKey)
    {
        lock (_sync)
        {
            string prefix = subKey.TrimEnd('\\') + "\\";
            var children = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RegistryValue value in _values.Values)
            {
                if (value.Hive != hive || !value.SubKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string remainder = value.SubKey[prefix.Length..];
                string child = remainder.Split('\\')[0];
                if (!string.IsNullOrWhiteSpace(child))
                {
                    children.Add(child);
                }
            }

            foreach (string key in _keys)
            {
                string normalized = key[(hive.ToString().Length + 1)..];
                if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string remainder = normalized[prefix.Length..];
                    string child = remainder.Split('\\')[0];
                    if (!string.IsNullOrWhiteSpace(child))
                    {
                        children.Add(child);
                    }
                }
            }

            return children.ToList();
        }
    }

    public bool KeyExists(RegistryHive hive, string subKey)
    {
        lock (_sync)
        {
            return _keys.Contains(Key(hive, subKey, string.Empty)) ||
                   _values.Values.Any(value => value.Hive == hive && PathEquals(value.SubKey, subKey));
        }
    }

    public void WriteValue(RegistryHive hive, string subKey, string valueName, RegistryValueKind kind, object data)
    {
        lock (_sync)
        {
            _values[Key(hive, subKey, valueName)] = new RegistryValue(hive, subKey, valueName, kind, data);
            _keys.Add(Key(hive, subKey, string.Empty));
            WriteCount++;
            Operations.Add($"write {hive}\\{subKey}\\{valueName}");
        }
    }

    public void DeleteValue(RegistryHive hive, string subKey, string valueName)
    {
        lock (_sync)
        {
            if (_values.Remove(Key(hive, subKey, valueName)))
            {
                DeleteCount++;
                Operations.Add($"delete {hive}\\{subKey}\\{valueName}");
            }
        }
    }

    public void CreateKey(RegistryHive hive, string subKey)
    {
        lock (_sync)
        {
            _keys.Add(Key(hive, subKey, string.Empty));
        }
    }

    public void SetDword(RegistryHive hive, string subKey, string valueName, uint value) =>
        WriteValue(hive, subKey, valueName, RegistryValueKind.DWord, RegistryValueCodec.ToRegistryDword(value));

    public void SetQword(RegistryHive hive, string subKey, string valueName, ulong value) =>
        WriteValue(hive, subKey, valueName, RegistryValueKind.QWord, unchecked((long)value));

    public void SetString(RegistryHive hive, string subKey, string valueName, string value) =>
        WriteValue(hive, subKey, valueName, RegistryValueKind.String, value);

    public void SetBinary(RegistryHive hive, string subKey, string valueName, byte[] value) =>
        WriteValue(hive, subKey, valueName, RegistryValueKind.Binary, value);

    public void Seed(RegistryValue value) =>
        WriteValue(value.Hive, value.SubKey, value.ValueName, value.Kind, value.Data);

    private static string Key(RegistryHive hive, string subKey, string valueName) =>
        $"{hive}|{subKey.TrimEnd('\\')}|{valueName}";

    private static bool PathEquals(string left, string right) =>
        string.Equals(left.TrimEnd('\\'), right.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
