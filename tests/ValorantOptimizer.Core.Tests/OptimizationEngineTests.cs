using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.TestSupport;
using Xunit;

namespace ValorantOptimizer.Core.Tests;

public sealed class OptimizationEngineTests
{
    private const string SubKey = @"Software\ValorantOptimizer\Test";
    private const string ValueName = "Setting";

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];

        public void Report(T value) => Items.Add(value);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(bool isElevated = false)
        {
            Backups = new JsonBackupStore(Temp.Path);
            Log = new JsonlActionLog(Temp.Path);
            Context = new OptimizationContext(Registry, new FakeProcessRunner(), isElevated);
        }

        public FakeRegistryStore Registry { get; } = new();

        public TempDirectory Temp { get; } = new();

        public JsonBackupStore Backups { get; }

        public JsonlActionLog Log { get; }

        public OptimizationContext Context { get; }

        public OptimizationEngine Create(params IOptimization[] optimizations) =>
            new(optimizations, Backups, Log, Context);

        public OptimizationEngine Create(IElevatedOperationClient client, params IOptimization[] optimizations) =>
            new(optimizations, Backups, Log, Context, client);

        public void Dispose() => Temp.Dispose();
    }

    private sealed class FakeElevatedClient : IElevatedOperationClient
    {
        private readonly Action<ElevatedOperationRequest>? _onExecute;

        public FakeElevatedClient(Action<ElevatedOperationRequest>? onExecute = null)
        {
            _onExecute = onExecute;
        }

        public List<ElevatedOperationRequest> Requests { get; } = [];

        public bool Cancel { get; set; }

        public bool Success { get; set; } = true;

        public Task<ElevatedOperationResponse> ExecuteAsync(
            ElevatedOperationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Cancel)
            {
                throw new ElevationCancelledException("Simulated UAC cancellation.");
            }

            _onExecute?.Invoke(request);
            return Task.FromResult(new ElevatedOperationResponse
            {
                RequestId = request.RequestId,
                Success = Success,
                Verified = Success,
                Message = "Simulated helper result.",
            });
        }
    }

    private static OptimizationDescriptor ElevatedDescriptor(string id = "fake.elevated") => new()
    {
        Id = id,
        Name = "Fake elevated setting",
        Category = OptimizationCategory.Windows,
        Risk = RiskLevel.Safe,
        Summary = "Test optimization requiring elevation.",
        WhatItChanges = "A harmless test value.",
        Why = "Engine tests.",
        RequiresElevation = true,
        ManagedScopes = [new RegistryScope(RegistryHive.LocalMachine, @"Software\ValorantOptimizer\Tests")],
    };

    [Fact]
    public async Task Apply_success_saves_backup_and_logs_verified()
    {
        using var fixture = new Fixture();
        var optimization = new FakeOptimization();
        OptimizationEngine engine = fixture.Create(optimization);

        OperationOutcome outcome = await engine.ApplyAsync(optimization.Descriptor.Id);

        Assert.True(outcome.Success);
        Assert.True(outcome.Verified);
        Assert.Equal(1, optimization.CaptureCalls);
        Assert.Equal(1, optimization.ApplyCalls);
        Assert.True(await fixture.Backups.HasBackupAsync(optimization.Descriptor.Id));

        IReadOnlyList<ActionLogEntry> log = await fixture.Log.ReadRecentAsync(10);
        ActionLogEntry entry = Assert.Single(log);
        Assert.Equal("apply", entry.Action);
        Assert.Equal("verified", entry.Result);
    }

    [Fact]
    public async Task Apply_already_applied_does_nothing()
    {
        using var fixture = new Fixture();
        var optimization = new FakeOptimization { State = OptimizationState.Applied };
        OptimizationEngine engine = fixture.Create(optimization);

        OperationOutcome outcome = await engine.ApplyAsync(optimization.Descriptor.Id);

        Assert.True(outcome.Success);
        Assert.True(outcome.Verified);
        Assert.Equal(0, optimization.CaptureCalls);
        Assert.Equal(0, fixture.Registry.WriteCount);
        Assert.False(await fixture.Backups.HasBackupAsync(optimization.Descriptor.Id));
    }

    [Theory]
    [InlineData(OptimizationState.Unsupported)]
    [InlineData(OptimizationState.ReviewRequired)]
    public async Task Apply_non_actionable_state_is_refused(OptimizationState state)
    {
        using var fixture = new Fixture();
        var optimization = new FakeOptimization { State = state };
        OptimizationEngine engine = fixture.Create(optimization);

        OperationOutcome outcome = await engine.ApplyAsync(optimization.Descriptor.Id);

        Assert.False(outcome.Success);
        Assert.Contains("Blocked by test", outcome.Message);
        Assert.Equal(0, optimization.CaptureCalls);
        Assert.Equal(0, optimization.ApplyCalls);
    }

    [Fact]
    public async Task Apply_unknown_state_is_refused()
    {
        using var fixture = new Fixture();
        var optimization = new FakeOptimization { State = OptimizationState.Unknown };
        OptimizationEngine engine = fixture.Create(optimization);

        OperationOutcome outcome = await engine.ApplyAsync(optimization.Descriptor.Id);

        Assert.False(outcome.Success);
        Assert.Equal(0, optimization.CaptureCalls);
    }

    [Fact]
    public async Task Apply_with_empty_capture_is_refused_before_any_write()
    {
        using var fixture = new Fixture();
        var optimization = new FakeOptimization { CapturedEntries = [] };
        OptimizationEngine engine = fixture.Create(optimization);

        OperationOutcome outcome = await engine.ApplyAsync(optimization.Descriptor.Id);

        Assert.False(outcome.Success);
        Assert.Contains("did not capture", outcome.Message);
        Assert.Equal(0, optimization.ApplyCalls);
        Assert.False(await fixture.Backups.HasBackupAsync(optimization.Descriptor.Id));
    }

    [Fact]
    public async Task Apply_failure_rolls_back_to_captured_state()
    {
        using var fixture = new Fixture();
        fixture.Registry.SetDword(RegistryHive.CurrentUser, SubKey, ValueName, 0);
        var optimization = new FakeOptimization
        {
            CapturedEntries =
            [
                new RegistrySnapshotEntry
                {
                    Hive = RegistryHive.CurrentUser,
                    SubKey = SubKey,
                    ValueName = ValueName,
                    Existed = true,
                    Kind = RegistryValueKind.DWord,
                    SerializedData = "0",
                },
            ],
            ApplySucceeds = false,
            OnApply = context => context.Registry.WriteValue(RegistryHive.CurrentUser, SubKey, ValueName, RegistryValueKind.DWord, 1),
            OnRestore = context => context.Registry.WriteValue(RegistryHive.CurrentUser, SubKey, ValueName, RegistryValueKind.DWord, 0),
        };
        OptimizationEngine engine = fixture.Create(optimization);

        OperationOutcome outcome = await engine.ApplyAsync(optimization.Descriptor.Id);

        Assert.False(outcome.Success);
        Assert.True(outcome.RolledBack);
        Assert.Equal(1, optimization.RestoreCalls);
        Assert.Equal("0", fixture.Registry.ReadValue(RegistryHive.CurrentUser, SubKey, ValueName)!.DisplayValue);

        IReadOnlyList<ActionLogEntry> log = await fixture.Log.ReadRecentAsync(10);
        Assert.Equal("rolled-back", Assert.Single(log).Result);
    }

    [Fact]
    public async Task Apply_exception_rolls_back_and_preserves_backup()
    {
        using var fixture = new Fixture();
        fixture.Registry.SetDword(RegistryHive.CurrentUser, SubKey, ValueName, 0);
        var optimization = new FakeOptimization
        {
            ApplyException = new InvalidOperationException("Simulated crash."),
            OnRestore = context => context.Registry.WriteValue(RegistryHive.CurrentUser, SubKey, ValueName, RegistryValueKind.DWord, 0),
        };
        OptimizationEngine engine = fixture.Create(optimization);

        OperationOutcome outcome = await engine.ApplyAsync(optimization.Descriptor.Id);

        Assert.False(outcome.Success);
        Assert.True(outcome.RolledBack, string.Join(" | ", outcome.Warnings));
        Assert.Contains("Simulated crash", outcome.Message);
        Assert.True(await fixture.Backups.HasBackupAsync(optimization.Descriptor.Id));
    }

    [Fact]
    public async Task Apply_unverified_state_reports_warning()
    {
        using var fixture = new Fixture();
        var optimization = new FakeOptimization { StateAfterApply = OptimizationState.NotApplied };
        OptimizationEngine engine = fixture.Create(optimization);

        OperationOutcome outcome = await engine.ApplyAsync(optimization.Descriptor.Id);

        Assert.True(outcome.Success);
        Assert.False(outcome.Verified);
        Assert.Contains(outcome.Warnings, warning => warning.Contains("could not be verified"));

        IReadOnlyList<ActionLogEntry> log = await fixture.Log.ReadRecentAsync(10);
        Assert.Equal("applied-unverified", Assert.Single(log).Result);
    }

    [Fact]
    public async Task Apply_requiring_elevation_uses_helper_and_not_local_apply()
    {
        using var fixture = new Fixture(isElevated: false);
        var optimization = new FakeOptimization(descriptor: ElevatedDescriptor());
        var client = new FakeElevatedClient(_ => optimization.State = OptimizationState.Applied);
        OptimizationEngine engine = fixture.Create(client, optimization);

        OperationOutcome outcome = await engine.ApplyAsync(optimization.Descriptor.Id);

        Assert.True(outcome.Success);
        Assert.True(outcome.Verified);
        Assert.Equal(0, optimization.ApplyCalls);
        ElevatedOperationRequest request = Assert.Single(client.Requests);
        Assert.Equal(ElevatedOperationKind.Apply, request.Operation);
        Assert.Equal(optimization.Descriptor.Id, request.OptimizationId);
        Assert.True(await fixture.Backups.HasBackupAsync(optimization.Descriptor.Id));
    }

    [Fact]
    public async Task Apply_elevation_cancelled_changes_nothing()
    {
        using var fixture = new Fixture(isElevated: false);
        var optimization = new FakeOptimization(descriptor: ElevatedDescriptor());
        var client = new FakeElevatedClient { Cancel = true };
        OptimizationEngine engine = fixture.Create(client, optimization);

        OperationOutcome outcome = await engine.ApplyAsync(optimization.Descriptor.Id);

        Assert.False(outcome.Success);
        Assert.Contains("cancelled", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, optimization.ApplyCalls);
    }

    [Fact]
    public async Task Undo_without_backup_fails_clearly()
    {
        using var fixture = new Fixture();
        var optimization = new FakeOptimization();
        OptimizationEngine engine = fixture.Create(optimization);

        OperationOutcome outcome = await engine.UndoAsync(optimization.Descriptor.Id);

        Assert.False(outcome.Success);
        Assert.Contains("No backup", outcome.Message);
        Assert.Equal(0, optimization.RestoreCalls);
    }

    [Fact]
    public async Task Undo_restores_original_values_and_verifies_them()
    {
        using var fixture = new Fixture();
        fixture.Registry.SetDword(RegistryHive.CurrentUser, SubKey, ValueName, 0);
        var optimization = new FakeOptimization
        {
            OnApply = context => context.Registry.WriteValue(RegistryHive.CurrentUser, SubKey, ValueName, RegistryValueKind.DWord, 1),
            OnRestore = context => context.Registry.WriteValue(RegistryHive.CurrentUser, SubKey, ValueName, RegistryValueKind.DWord, 0),
        };
        OptimizationEngine engine = fixture.Create(optimization);

        OperationOutcome applied = await engine.ApplyAsync(optimization.Descriptor.Id);
        Assert.True(applied.Success);
        Assert.Equal("1", fixture.Registry.ReadValue(RegistryHive.CurrentUser, SubKey, ValueName)!.DisplayValue);

        OperationOutcome undone = await engine.UndoAsync(optimization.Descriptor.Id);

        Assert.True(undone.Success);
        Assert.True(undone.Verified, string.Join(" | ", undone.Warnings));
        Assert.Equal(1, optimization.RestoreCalls);
        Assert.Equal("0", fixture.Registry.ReadValue(RegistryHive.CurrentUser, SubKey, ValueName)!.DisplayValue);

        IReadOnlyList<ActionLogEntry> log = await fixture.Log.ReadRecentAsync(10);
        Assert.Equal("restore", log[0].Action);
        Assert.Equal("verified", log[0].Result);
    }

    [Fact]
    public async Task Undo_rejects_backup_entries_outside_declared_scope()
    {
        using var fixture = new Fixture();
        var optimization = new FakeOptimization();
        var backup = new ChangeSet
        {
            OptimizationId = optimization.Descriptor.Id,
            Entries =
            [
                new RegistrySnapshotEntry
                {
                    Hive = RegistryHive.LocalMachine,
                    SubKey = @"Software\SomeoneElse",
                    ValueName = "Unsafe",
                    Existed = true,
                    Kind = RegistryValueKind.DWord,
                    SerializedData = "1",
                },
            ],
        };
        await fixture.Backups.SaveAsync(backup);
        OptimizationEngine engine = fixture.Create(optimization);

        OperationOutcome outcome = await engine.UndoAsync(optimization.Descriptor.Id);

        Assert.False(outcome.Success);
        Assert.Contains("outside the declared scopes", outcome.Message);
        Assert.Equal(0, optimization.RestoreCalls);
    }

    [Fact]
    public async Task AssessAll_reports_backup_availability_and_progress()
    {
        using var fixture = new Fixture();
        var applied = new FakeOptimization("fake.applied");
        var available = new FakeOptimization("fake.available");
        OptimizationEngine engine = fixture.Create(applied, available);
        await engine.ApplyAsync(applied.Descriptor.Id);

        var progress = new RecordingProgress<OptimizationStatus>();
        IReadOnlyList<OptimizationStatus> statuses = await engine.AssessAllAsync(progress);

        Assert.Equal(2, statuses.Count);
        Assert.Equal(2, progress.Items.Count);
        Assert.True(statuses.Single(status => status.Optimization.Descriptor.Id == "fake.applied").HasBackup);
        Assert.False(statuses.Single(status => status.Optimization.Descriptor.Id == "fake.available").HasBackup);
    }

    [Fact]
    public async Task Apply_unknown_optimization_id_throws()
    {
        using var fixture = new Fixture();
        OptimizationEngine engine = fixture.Create(new FakeOptimization());

        await Assert.ThrowsAsync<ArgumentException>(() => engine.ApplyAsync("does.not.exist"));
    }
}
