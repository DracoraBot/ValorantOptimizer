using System.Text.Json;
using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Core.Tests;

internal static class TestChangeSets
{
    public static ChangeSet Create(string optimizationId = "fake.setting") => new()
    {
        OptimizationId = optimizationId,
        CapturedAt = DateTimeOffset.Now,
        Entries =
        [
            new RegistrySnapshotEntry
            {
                Hive = RegistryHive.CurrentUser,
                SubKey = @"Software\ValorantOptimizer\Tests",
                ValueName = "Enabled",
                Existed = true,
                Kind = RegistryValueKind.DWord,
                SerializedData = "0",
            },
        ],
        Metadata = new Dictionary<string, string> { ["scheme"] = "381b4222-f694-41f0-9685-ff5bb260df2e" },
    };
}

public sealed class JsonBackupStoreTests
{
    [Fact]
    public async Task Save_and_load_round_trips_all_fields()
    {
        using var temp = new TempDirectory();
        var store = new JsonBackupStore(temp.Path);
        ChangeSet original = TestChangeSets.Create();

        await store.SaveAsync(original);
        ChangeSet? loaded = await store.TryLoadAsync(original.OptimizationId);

        Assert.NotNull(loaded);
        Assert.Equal(original.OptimizationId, loaded!.OptimizationId);
        Assert.Equal(original.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(original.CapturedAt.ToUnixTimeMilliseconds(), loaded.CapturedAt.ToUnixTimeMilliseconds());
        Assert.Equal(original.Entries.Count, loaded.Entries.Count);
        Assert.Equal(original.Entries[0].Existed, loaded.Entries[0].Existed);
        Assert.Equal(original.Entries[0].Kind, loaded.Entries[0].Kind);
        Assert.Equal(original.Entries[0].SerializedData, loaded.Entries[0].SerializedData);
        Assert.Equal(original.Metadata["scheme"], loaded.Metadata["scheme"]);
    }

    [Fact]
    public async Task TryLoad_returns_null_when_no_backup_exists()
    {
        using var temp = new TempDirectory();
        var store = new JsonBackupStore(temp.Path);

        Assert.Null(await store.TryLoadAsync("missing.setting"));
        Assert.False(await store.HasBackupAsync("missing.setting"));
    }

    [Fact]
    public async Task HasBackup_and_Delete_work()
    {
        using var temp = new TempDirectory();
        var store = new JsonBackupStore(temp.Path);
        ChangeSet set = TestChangeSets.Create();
        await store.SaveAsync(set);

        Assert.True(await store.HasBackupAsync(set.OptimizationId));

        await store.DeleteAsync(set.OptimizationId);

        Assert.False(await store.HasBackupAsync(set.OptimizationId));
        Assert.Null(await store.TryLoadAsync(set.OptimizationId));
    }

    [Fact]
    public async Task Save_rejects_invalid_id()
    {
        using var temp = new TempDirectory();
        var store = new JsonBackupStore(temp.Path);
        ChangeSet set = TestChangeSets.Create("../evil");

        await Assert.ThrowsAsync<BackupStoreException>(() => store.SaveAsync(set));
    }

    [Fact]
    public async Task Save_rejects_future_schema()
    {
        using var temp = new TempDirectory();
        var store = new JsonBackupStore(temp.Path);
        ChangeSet set = TestChangeSets.Create() with { SchemaVersion = 99 };

        await Assert.ThrowsAsync<BackupStoreException>(() => store.SaveAsync(set));
    }

    [Fact]
    public async Task Load_rejects_future_schema_on_disk()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Path);
        ChangeSet set = TestChangeSets.Create() with { SchemaVersion = 99 };
        string path = Path.Combine(temp.Path, set.OptimizationId + ".backup.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(set, JsonDefaults.Options));
        var store = new JsonBackupStore(temp.Path);

        BackupStoreException ex = await Assert.ThrowsAsync<BackupStoreException>(() => store.TryLoadAsync(set.OptimizationId));
        Assert.Contains("schema version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_rejects_corrupt_json()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Path);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "broken.setting.backup.json"), "{ not json");
        var store = new JsonBackupStore(temp.Path);

        await Assert.ThrowsAsync<BackupStoreException>(() => store.TryLoadAsync("broken.setting"));
    }

    [Fact]
    public async Task LoadAll_skips_corrupt_files_but_returns_valid_ones()
    {
        using var temp = new TempDirectory();
        var store = new JsonBackupStore(temp.Path);
        ChangeSet valid = TestChangeSets.Create();
        await store.SaveAsync(valid);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "broken.backup.json"), "{ not json");

        IReadOnlyList<ChangeSet> all = await store.LoadAllAsync();

        Assert.Single(all);
        Assert.Equal(valid.OptimizationId, all[0].OptimizationId);
    }

    [Fact]
    public async Task Save_is_atomic_and_overwrites_previous_backup()
    {
        using var temp = new TempDirectory();
        var store = new JsonBackupStore(temp.Path);
        ChangeSet first = TestChangeSets.Create();
        await store.SaveAsync(first);

        ChangeSet second = first with { CapturedAt = first.CapturedAt.AddMinutes(5) };
        await store.SaveAsync(second);

        ChangeSet? loaded = await store.TryLoadAsync(first.OptimizationId);
        Assert.NotNull(loaded);
        Assert.Equal(second.CapturedAt.ToUnixTimeMilliseconds(), loaded!.CapturedAt.ToUnixTimeMilliseconds());
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }
}

public sealed class JsonlActionLogTests
{
    [Fact]
    public async Task Append_and_read_returns_newest_first()
    {
        using var temp = new TempDirectory();
        var log = new JsonlActionLog(temp.Path);
        await log.AppendAsync(new ActionLogEntry
        {
            OptimizationId = "fake.setting",
            OptimizationName = "Fake",
            Action = "apply",
            Result = "verified",
            Message = "first",
        });
        await log.AppendAsync(new ActionLogEntry
        {
            OptimizationId = "fake.setting",
            OptimizationName = "Fake",
            Action = "restore",
            Result = "verified",
            Message = "second",
        });

        IReadOnlyList<ActionLogEntry> entries = await log.ReadRecentAsync(10);

        Assert.Equal(2, entries.Count);
        Assert.Equal("second", entries[0].Message);
        Assert.Equal("first", entries[1].Message);
    }

    [Fact]
    public async Task ReadRecent_skips_malformed_lines()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Path);
        var log = new JsonlActionLog(temp.Path);
        await log.AppendAsync(new ActionLogEntry
        {
            OptimizationId = "fake.setting",
            OptimizationName = "Fake",
            Action = "apply",
            Result = "verified",
        });
        await File.AppendAllTextAsync(Path.Combine(temp.Path, "actions.jsonl"), "{ broken" + Environment.NewLine);

        IReadOnlyList<ActionLogEntry> entries = await log.ReadRecentAsync(10);

        Assert.Single(entries);
    }

    [Fact]
    public async Task ReadRecent_handles_missing_file_and_zero_limit()
    {
        using var temp = new TempDirectory();
        var log = new JsonlActionLog(temp.Path);

        Assert.Empty(await log.ReadRecentAsync(10));
        Assert.Empty(await log.ReadRecentAsync(0));
    }
}
