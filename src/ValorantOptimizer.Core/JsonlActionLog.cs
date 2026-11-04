using System.Text;
using System.Text.Json;

namespace ValorantOptimizer.Core;

/// <summary>One inspectable history record, written as a JSON line.</summary>
public sealed record ActionLogEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public required string OptimizationId { get; init; }

    public required string OptimizationName { get; init; }

    /// <summary>"apply" or "restore".</summary>
    public required string Action { get; init; }

    /// <summary>"verified", "applied-unverified", "failed", "rolled-back".</summary>
    public required string Result { get; init; }

    public string? Message { get; init; }

    public List<string> Changes { get; init; } = [];
}

public interface IActionLog
{
    Task AppendAsync(ActionLogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<ActionLogEntry>> ReadRecentAsync(int maxEntries, CancellationToken cancellationToken = default);
}

/// <summary>
/// Append-only local action history. Deliberately human-readable so users can inspect exactly
/// what the app changed without opening a proprietary store.
/// </summary>
public sealed class JsonlActionLog : IActionLog
{
    private static readonly JsonSerializerOptions CompactOptions = new(JsonDefaults.Options)
    {
        WriteIndented = false,
    };

    private readonly string _directory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonlActionLog(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    public string LogFilePath => Path.Combine(_directory, "actions.jsonl");

    public async Task AppendAsync(ActionLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Directory.CreateDirectory(_directory);

        string line = JsonSerializer.Serialize(entry, CompactOptions) + Environment.NewLine;
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(LogFilePath, line, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<ActionLogEntry>> ReadRecentAsync(int maxEntries, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxEntries);
        if (maxEntries == 0 || !File.Exists(LogFilePath))
        {
            return [];
        }

        string[] lines = await File.ReadAllLinesAsync(LogFilePath, cancellationToken).ConfigureAwait(false);
        var entries = new List<ActionLogEntry>(Math.Min(maxEntries, lines.Length));
        for (int i = lines.Length - 1; i >= 0 && entries.Count < maxEntries; i--)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                ActionLogEntry? entry = JsonSerializer.Deserialize<ActionLogEntry>(line, JsonDefaults.Options);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // Skip malformed lines instead of hiding the rest of the history.
            }
        }

        return entries;
    }
}
