using Microsoft.Win32;

namespace ValorantOptimizer.Core;

/// <summary>
/// One registry value captured before a change. <see cref="SerializedData"/> uses the canonical
/// encoding from <see cref="RegistryValueCodec"/> so backups round-trip exactly.
/// </summary>
public sealed record RegistrySnapshotEntry
{
    public required RegistryHive Hive { get; init; }

    public required string SubKey { get; init; }

    public required string ValueName { get; init; }

    /// <summary>False when the value did not exist before the change.</summary>
    public required bool Existed { get; init; }

    public RegistryValueKind Kind { get; init; } = RegistryValueKind.Unknown;

    /// <summary>Canonical string encoding of the original data. Null when !Existed.</summary>
    public string? SerializedData { get; init; }

    public override string ToString()
    {
        var target = $@"{Hive}\{SubKey}\{ValueName}";
        return Existed ? $@"{target} = {SerializedData}" : $@"{target} (not present)";
    }
}

/// <summary>
/// Exact original state captured before an optimization was applied. Persisted as JSON with a
/// schema version so future versions can migrate or reject incompatible backups.
/// </summary>
public sealed record ChangeSet
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string OptimizationId { get; init; }

    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;

    public List<RegistrySnapshotEntry> Entries { get; init; } = [];

    /// <summary>
    /// Non-registry state (for example the active power scheme GUID). Values must be strings so
    /// the backup format remains simple and auditable.
    /// </summary>
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
