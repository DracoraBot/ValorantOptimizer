namespace ValorantOptimizer.Diagnostics;

public enum StartupSource
{
    UserRunKey,
    MachineRunKey,
    UserStartupFolder,
    MachineStartupFolder,
    ScheduledTask,
}

/// <summary>One application that starts with Windows. Read-only review data.</summary>
public sealed record StartupAppEntry
{
    public required string Name { get; init; }

    public required string Command { get; init; }

    public required StartupSource Source { get; init; }

    public required bool IsEnabled { get; init; }

    public string? Publisher { get; init; }

    /// <summary>Risk/identity annotations, e.g. "Anti-cheat", "Audio driver", "System component".</summary>
    public List<string> Flags { get; init; } = [];

    public string? Note { get; init; }
}

public interface IStartupAppScanner
{
    Task<IReadOnlyList<StartupAppEntry>> ScanAsync(CancellationToken cancellationToken = default);
}
