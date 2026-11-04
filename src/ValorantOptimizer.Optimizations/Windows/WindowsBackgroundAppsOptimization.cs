using System.Globalization;
using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Stops background/UWP apps from running background tasks for the current user by setting the
/// per-user BackgroundAccessApplications override. Foreground apps and desktop programs are not
/// affected.
/// </summary>
public sealed class WindowsBackgroundAppsOptimization : IOptimization
{
    public const string OptimizationId = "windows-background-apps";
    public const string RegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications";
    public const string ValueName = "GlobalUserDisabled";

    private const int DisabledValue = 1;

    private static readonly RegistryTarget Target =
        new(RegistryHive.CurrentUser, RegistrySubKey, ValueName);

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = OptimizationId,
        Name = "Disable background apps",
        Category = OptimizationCategory.Windows,
        Risk = RiskLevel.Safe,
        Summary = "Stops Windows apps from running background tasks for this user, reducing background activity while you play.",
        WhatItChanges = @"Writes the per-user DWORD HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\GlobalUserDisabled = 1. Undo restores the previous value, or deletes it when it did not exist. No administrator rights are required.",
        Why = "Many Windows apps keep timers, notifications and network work scheduled while they are not on screen. Disabling background app access for the user stops that background activity, which can free a small amount of CPU, memory and network time. It does not affect foreground apps, classic desktop programs or VALORANT itself, and no specific frame-rate gain is promised.",
        Compatibility = "Windows 10 and Windows 11. This is a per-user preference and requires no administrator rights. Some apps that need background access (for example mail, chat or media apps) stop refreshing and showing notifications until opened; other accounts on the same PC keep their own setting.",
        StateWhenApplied = "Background apps off",
        Restart = RestartRequirement.None,
        RequiresElevation = false,
        ManagedScopes = [new RegistryScope(RegistryHive.CurrentUser, RegistrySubKey)],
        Notes =
        [
            "Per-user setting: other accounts on this PC keep their own background-app preference.",
            "Background notifications for store apps can stop while this is enabled; apps you open in the foreground still work normally.",
            "Windows Settings > Privacy & security > Background apps exposes per-app controls that can override this global value.",
            "Reversible: Undo restores the previous value, or deletes it when Windows did not have one, returning to the default behavior.",
        ],
    };

    public Task<OptimizationAssessment> AssessAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        RegistryValue? value = context.Registry.ReadValue(
            RegistryHive.CurrentUser,
            RegistrySubKey,
            ValueName);

        var evidence = new List<AssessmentEvidence>
        {
            new(ValueName, Describe(value)),
        };

        OptimizationAssessment assessment;
        if (value is null)
        {
            assessment = OptimizationAssessment.Available(
                "Background apps allowed (Windows default)",
                "GlobalUserDisabled is not set, so Windows keeps background app access at its default, which allows apps to run background tasks.");
        }
        else if (value.Kind != RegistryValueKind.DWord)
        {
            assessment = OptimizationAssessment.Available(
                "Background apps allowed (unexpected format)",
                $"GlobalUserDisabled is stored as {value.Kind} instead of DWORD, so the recommended value 1 is not in effect. Applying writes DWORD 1 and Undo restores the original kind and data.");
        }
        else
        {
            int current = Convert.ToInt32(value.Data, CultureInfo.InvariantCulture);
            assessment = current == DisabledValue
                ? OptimizationAssessment.Applied(
                    "Background apps off",
                    "GlobalUserDisabled is 1, so Windows does not run background tasks for store apps for this user.")
                : OptimizationAssessment.Available(
                    "Background apps allowed",
                    $"GlobalUserDisabled is {current} (0 means background apps are allowed). Applying sets it to 1.");
        }

        return Task.FromResult(assessment with { Evidence = evidence });
    }

    public Task<ChangeSet> CaptureAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var original = new ChangeSet
        {
            OptimizationId = Descriptor.Id,
            Entries = RegistrySnapshot.CaptureMany(context.Registry, Target),
        };

        return Task.FromResult(original);
    }

    public Task<OperationOutcome> ApplyAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(original);
        cancellationToken.ThrowIfCancellationRequested();

        context.Registry.WriteValue(
            RegistryHive.CurrentUser,
            RegistrySubKey,
            ValueName,
            RegistryValueKind.DWord,
            DisabledValue);

        return Task.FromResult(OperationOutcome.Ok(
            "Disabled Windows background apps for this user.") with
        {
            Changes =
            [
                $@"{RegistryHive.CurrentUser}\{RegistrySubKey}\{ValueName} = {DisabledValue} (REG_DWORD)",
            ],
        });
    }

    public Task<OperationOutcome> RestoreAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(original);
        cancellationToken.ThrowIfCancellationRequested();

        RegistrySnapshot.RestoreAll(context.Registry, original.Entries);

        return Task.FromResult(OperationOutcome.Ok(
            "Restored the original background-app setting.") with
        {
            Changes = original.Entries.Select(entry => entry.ToString()).ToList(),
        });
    }

    private static string Describe(RegistryValue? value)
    {
        if (value is null)
        {
            return "Not set (Windows default: background apps allowed)";
        }

        if (value.Kind == RegistryValueKind.DWord)
        {
            int current = Convert.ToInt32(value.Data, CultureInfo.InvariantCulture);
            return current switch
            {
                DisabledValue => "1 (background apps disabled)",
                0 => "0 (background apps allowed)",
                _ => $"{current} (unrecognized; only 1 disables background apps)",
            };
        }

        return $"{RegistryValueCodec.Serialize(value.Data, value.Kind)} ({value.Kind})";
    }
}
