using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.TestSupport;

/// <summary>Configurable in-memory optimization used to test engine safety behavior.</summary>
public sealed class FakeOptimization : IOptimization
{
    public FakeOptimization(
        string id = "fake.setting",
        string name = "Fake setting",
        OptimizationDescriptor? descriptor = null)
    {
        Descriptor = descriptor ?? new OptimizationDescriptor
        {
            Id = id,
            Name = name,
            Category = OptimizationCategory.Windows,
            Risk = RiskLevel.Safe,
            Summary = "Test optimization.",
            WhatItChanges = "A harmless test value.",
            Why = "Engine tests.",
            ManagedScopes = [new RegistryScope(RegistryHive.CurrentUser, @"Software\ValorantOptimizer\Test")],
        };

        CapturedEntries =
        [
            new RegistrySnapshotEntry
            {
                Hive = RegistryHive.CurrentUser,
                SubKey = @"Software\ValorantOptimizer\Test",
                ValueName = "Setting",
                Existed = true,
                Kind = RegistryValueKind.DWord,
                SerializedData = "0",
            },
        ];
    }

    public OptimizationDescriptor Descriptor { get; }

    public OptimizationState State { get; set; } = OptimizationState.NotApplied;

    public OptimizationState StateAfterApply { get; set; } = OptimizationState.Applied;

    public OptimizationState StateAfterRestore { get; set; } = OptimizationState.NotApplied;

    public string Headline { get; set; } = "Not configured";

    /// <summary>Optional custom assessment, used to simulate state that changes in another process.</summary>
    public Func<OptimizationAssessment>? AssessmentFactory { get; set; }

    public bool ApplySucceeds { get; set; } = true;

    public bool RestoreSucceeds { get; set; } = true;

    public Exception? ApplyException { get; set; }

    public Exception? RestoreException { get; set; }

    public List<RegistrySnapshotEntry> CapturedEntries { get; set; }

    public Action<IOptimizationContext>? OnApply { get; set; }

    public Action<IOptimizationContext>? OnRestore { get; set; }

    public int AssessCalls { get; private set; }

    public int CaptureCalls { get; private set; }

    public int ApplyCalls { get; private set; }

    public int RestoreCalls { get; private set; }

    public ChangeSet? LastCaptured { get; private set; }

    public bool ThrowOnAssess { get; set; }

    public Task<OptimizationAssessment> AssessAsync(IOptimizationContext context, CancellationToken cancellationToken = default)
    {
        AssessCalls++;
        if (ThrowOnAssess)
        {
            throw new InvalidOperationException("Simulated assessment failure.");
        }

        if (AssessmentFactory is not null)
        {
            return Task.FromResult(AssessmentFactory());
        }

        return Task.FromResult(new OptimizationAssessment
        {
            State = State,
            Headline = Headline,
            BlockedReason = State is OptimizationState.Unsupported or OptimizationState.ReviewRequired ? "Blocked by test." : null,
        });
    }

    public Task<ChangeSet> CaptureAsync(IOptimizationContext context, CancellationToken cancellationToken = default)
    {
        CaptureCalls++;
        LastCaptured = new ChangeSet
        {
            OptimizationId = Descriptor.Id,
            Entries = CapturedEntries,
        };
        return Task.FromResult(LastCaptured);
    }

    public Task<OperationOutcome> ApplyAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default)
    {
        ApplyCalls++;
        if (ApplyException is not null)
        {
            throw ApplyException;
        }

        if (!ApplySucceeds)
        {
            return Task.FromResult(OperationOutcome.Failed("Simulated apply failure."));
        }

        OnApply?.Invoke(context);
        State = StateAfterApply;
        Headline = "Configured";
        return Task.FromResult(OperationOutcome.Ok("Applied by test."));
    }

    public Task<OperationOutcome> RestoreAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default)
    {
        RestoreCalls++;
        if (RestoreException is not null)
        {
            throw RestoreException;
        }

        if (!RestoreSucceeds)
        {
            return Task.FromResult(OperationOutcome.Failed("Simulated restore failure."));
        }

        OnRestore?.Invoke(context);
        State = StateAfterRestore;
        Headline = "Not configured";
        return Task.FromResult(OperationOutcome.Ok("Restored by test."));
    }
}
