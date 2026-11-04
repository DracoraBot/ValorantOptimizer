using Microsoft.Win32;

namespace ValorantOptimizer.Core;

/// <summary>Identifies one registry value to capture.</summary>
public sealed record RegistryTarget(RegistryHive Hive, string SubKey, string ValueName);

/// <summary>
/// Helpers that implement the capture/restore half of the optimization lifecycle so individual
/// modules cannot accidentally capture or restore values differently.
/// </summary>
public static class RegistrySnapshot
{
    public static RegistrySnapshotEntry Capture(IRegistryStore store, RegistryHive hive, string subKey, string valueName)
    {
        RegistryValue? value = store.ReadValue(hive, subKey, valueName);
        return value is null
            ? new RegistrySnapshotEntry
            {
                Hive = hive,
                SubKey = subKey,
                ValueName = valueName,
                Existed = false,
            }
            : new RegistrySnapshotEntry
            {
                Hive = hive,
                SubKey = subKey,
                ValueName = valueName,
                Existed = true,
                Kind = value.Kind,
                SerializedData = RegistryValueCodec.Serialize(value.Data, value.Kind),
            };
    }

    public static RegistrySnapshotEntry Capture(IRegistryStore store, RegistryTarget target) =>
        Capture(store, target.Hive, target.SubKey, target.ValueName);

    public static List<RegistrySnapshotEntry> CaptureMany(IRegistryStore store, params RegistryTarget[] targets)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(targets);
        return targets.Select(target => Capture(store, target)).ToList();
    }

    public static void Restore(IRegistryStore store, RegistrySnapshotEntry entry)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(entry);

        if (!entry.Existed)
        {
            store.DeleteValue(entry.Hive, entry.SubKey, entry.ValueName);
            return;
        }

        object data = RegistryValueCodec.Deserialize(entry.SerializedData ?? string.Empty, entry.Kind);
        store.WriteValue(entry.Hive, entry.SubKey, entry.ValueName, entry.Kind, data);
    }

    public static void RestoreAll(IRegistryStore store, IEnumerable<RegistrySnapshotEntry> entries)
    {
        foreach (RegistrySnapshotEntry entry in entries)
        {
            Restore(store, entry);
        }
    }
}
