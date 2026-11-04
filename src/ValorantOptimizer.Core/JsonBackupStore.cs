using System.Text.Json;

namespace ValorantOptimizer.Core;

public sealed class BackupStoreException : Exception
{
    public BackupStoreException(string message) : base(message) { }
    public BackupStoreException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Persists one <see cref="ChangeSet"/> per optimization in a per-user folder. Backups are written
/// before a change is applied so a crash mid-apply still leaves a path back to the original state.
/// </summary>
public interface IBackupStore
{
    string DirectoryPath { get; }

    Task SaveAsync(ChangeSet changeSet, CancellationToken cancellationToken = default);

    /// <summary>Returns the stored backup, or null when none exists for the optimization.</summary>
    Task<ChangeSet?> TryLoadAsync(string optimizationId, CancellationToken cancellationToken = default);

    Task<bool> HasBackupAsync(string optimizationId, CancellationToken cancellationToken = default);

    Task DeleteAsync(string optimizationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChangeSet>> LoadAllAsync(CancellationToken cancellationToken = default);
}

public sealed class JsonBackupStore : IBackupStore
{
    public JsonBackupStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = directoryPath;
    }

    public string DirectoryPath { get; }

    public async Task SaveAsync(ChangeSet changeSet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        Validate(changeSet, expectedOptimizationId: null);

        Directory.CreateDirectory(DirectoryPath);
        string path = GetPath(changeSet.OptimizationId);
        string tempPath = path + ".tmp";
        string json = JsonSerializer.Serialize(changeSet, JsonDefaults.Options);

        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }

    public async Task<ChangeSet?> TryLoadAsync(string optimizationId, CancellationToken cancellationToken = default)
    {
        string path = GetPath(optimizationId);
        if (!File.Exists(path))
        {
            return null;
        }

        ChangeSet changeSet = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        Validate(changeSet, expectedOptimizationId: optimizationId);
        return changeSet;
    }

    public Task<bool> HasBackupAsync(string optimizationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(GetPath(optimizationId)));
    }

    public Task DeleteAsync(string optimizationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = GetPath(optimizationId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<ChangeSet>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return [];
        }

        var results = new List<ChangeSet>();
        foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.backup.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ChangeSet changeSet = await ReadAsync(file, cancellationToken).ConfigureAwait(false);
                Validate(changeSet, expectedOptimizationId: null);
                results.Add(changeSet);
            }
            catch (Exception ex) when (ex is JsonException or BackupStoreException)
            {
                // A corrupt or future-schema backup must not break the history view. The specific
                // optimization load surfaces a detailed error instead.
            }
        }

        return results;
    }

    private static async Task<ChangeSet> ReadAsync(string path, CancellationToken cancellationToken)
    {
        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<ChangeSet>(json, JsonDefaults.Options)
                ?? throw new BackupStoreException($"Backup file '{Path.GetFileName(path)}' was empty.");
        }
        catch (JsonException ex)
        {
            throw new BackupStoreException($"Backup file '{Path.GetFileName(path)}' is not valid JSON.", ex);
        }
    }

    private static void Validate(ChangeSet changeSet, string? expectedOptimizationId)
    {
        if (!IsValidOptimizationId(changeSet.OptimizationId))
        {
            throw new BackupStoreException($"Backup has an invalid optimization id '{changeSet.OptimizationId}'.");
        }

        if (expectedOptimizationId is not null &&
            !string.Equals(changeSet.OptimizationId, expectedOptimizationId, StringComparison.Ordinal))
        {
            throw new BackupStoreException(
                $"Backup id '{changeSet.OptimizationId}' does not match requested id '{expectedOptimizationId}'.");
        }

        if (changeSet.SchemaVersion != ChangeSet.CurrentSchemaVersion)
        {
            throw new BackupStoreException(
                $"Backup for '{changeSet.OptimizationId}' uses schema version {changeSet.SchemaVersion}; " +
                $"this build supports version {ChangeSet.CurrentSchemaVersion}.");
        }
    }

    private string GetPath(string optimizationId)
    {
        if (!IsValidOptimizationId(optimizationId))
        {
            throw new ArgumentException($"Invalid optimization id '{optimizationId}'.", nameof(optimizationId));
        }

        return Path.Combine(DirectoryPath, optimizationId + ".backup.json");
    }

    private static bool IsValidOptimizationId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length is < 3 or > 80)
        {
            return false;
        }

        foreach (char c in id)
        {
            bool valid = char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-';
            if (!valid)
            {
                return false;
            }
        }

        return char.IsAsciiLetterLower(id[0]) || char.IsAsciiDigit(id[0]);
    }
}
