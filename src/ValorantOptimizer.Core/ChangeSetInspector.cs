using Microsoft.Win32;

namespace ValorantOptimizer.Core;

/// <summary>
/// Compares the live machine against a captured <see cref="ChangeSet"/>. Used after Undo to prove
/// that every value matches the original state rather than trusting a module's own claim.
/// </summary>
public static class ChangeSetInspector
{
    public static bool Matches(IRegistryStore registry, ChangeSet changeSet, out List<string> mismatches)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(changeSet);

        mismatches = [];
        foreach (RegistrySnapshotEntry entry in changeSet.Entries)
        {
            RegistryValue? current = registry.ReadValue(entry.Hive, entry.SubKey, entry.ValueName);

            if (!entry.Existed)
            {
                if (current is not null)
                {
                    mismatches.Add($"{entry} is still present.");
                }

                continue;
            }

            if (current is null)
            {
                mismatches.Add($"{entry} is missing.");
                continue;
            }

            if (current.Kind != entry.Kind)
            {
                mismatches.Add($"{entry} has kind {current.Kind}, expected {entry.Kind}.");
                continue;
            }

            string expected = entry.SerializedData ?? string.Empty;
            string actual = RegistryValueCodec.Serialize(current.Data, current.Kind);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                mismatches.Add($"{entry} is '{actual}', expected '{expected}'.");
            }
        }

        return mismatches.Count == 0;
    }

    public static List<string> Describe(ChangeSet changeSet) =>
        changeSet.Entries.Select(entry => entry.ToString()).ToList();

    public static List<string> Describe(IEnumerable<RegistrySnapshotEntry> entries) =>
        entries.Select(entry => entry.ToString()).ToList();
}

/// <summary>
/// Rejects restore data that points outside the optimization's declared registry scopes.
/// The elevated helper uses this before writing anything supplied by the UI process.
/// </summary>
public static class RestoreScopeValidator
{
    public static bool IsValid(OptimizationDescriptor descriptor, ChangeSet changeSet, out string error)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(changeSet);

        if (!string.Equals(descriptor.Id, changeSet.OptimizationId, StringComparison.Ordinal))
        {
            error = $"Backup belongs to '{changeSet.OptimizationId}', not '{descriptor.Id}'.";
            return false;
        }

        if (changeSet.SchemaVersion != ChangeSet.CurrentSchemaVersion)
        {
            error = $"Backup schema version {changeSet.SchemaVersion} is not supported.";
            return false;
        }

        foreach (RegistrySnapshotEntry entry in changeSet.Entries)
        {
            if (entry.Existed && entry.Kind == RegistryValueKind.Unknown)
            {
                error = $"Backup entry '{entry}' has an unknown value kind.";
                return false;
            }

            if (!descriptor.ManagedScopes.Any(scope => scope.Contains(entry.Hive, entry.SubKey)))
            {
                error = $"Backup entry '{entry}' is outside the declared scopes of '{descriptor.Id}'.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }
}
