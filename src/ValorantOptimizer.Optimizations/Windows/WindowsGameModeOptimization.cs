using System.Globalization;
using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Keeps Windows Game Mode explicitly enabled for the current user. When the value is absent,
/// Windows already treats Game Mode as on, so the module reports the effective default as applied.
/// </summary>
public sealed class WindowsGameModeOptimization : IOptimization
{
    public const string OptimizationId = "windows-game-mode";
    public const string RegistrySubKey = @"Software\Microsoft\GameBar";
    public const string ValueName = "AutoGameModeEnabled";

    private const int EnabledValue = 1;

    private static readonly RegistryTarget Target =
        new(RegistryHive.CurrentUser, RegistrySubKey, ValueName);

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = OptimizationId,
        Name = "Windows Game Mode",
        Category = OptimizationCategory.Windows,
        Risk = RiskLevel.Safe,
        Summary = "Keeps Windows Game Mode enabled so Windows prioritizes the foreground game and reduces background interruptions while VALORANT has focus.",
        WhatItChanges = @"Writes the per-user DWORD HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled = 1. Undo restores the previous value, or deletes it when it did not exist.",
        Why = "Game Mode tells Windows to reduce background work and suppression of notifications while a game is in the foreground. It is a low-risk preference; its effect varies with the system and the game, and no specific frame-rate gain is promised.",
        Compatibility = "Windows 10 (version 1803 or later) and Windows 11. Per-user setting; no administrator rights are required. Managed or stripped-down builds that remove the Game Bar/Game Mode component keep the value but ignore it.",
        StateWhenApplied = "Game Mode on",
        Restart = RestartRequirement.None,
        RequiresElevation = false,
        ManagedScopes = [new RegistryScope(RegistryHive.CurrentUser, RegistrySubKey)],
        Notes =
        [
            "When AutoGameModeEnabled is missing, Windows enables Game Mode by default; the module reports \"On (Windows default)\" so the card matches the effective state.",
            "The Windows Settings toggle writes the same value, so a manual change there changes the assessment; reassess before using Undo.",
            "Game Mode is not a guaranteed performance increase; it is included because it is broadly safe and reversible.",
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

        AssessmentEvidence evidence = new(ValueName, Describe(value));

        OptimizationAssessment assessment;
        if (value is null)
        {
            assessment = OptimizationAssessment.Applied(
                "On (Windows default)",
                "No explicit Game Mode override is stored; Windows enables Game Mode by default when AutoGameModeEnabled is missing.");
        }
        else if (value.Kind != RegistryValueKind.DWord)
        {
            assessment = OptimizationAssessment.Available(
                "Off (unexpected value format)",
                $"AutoGameModeEnabled is stored as {value.Kind} instead of DWORD, so the recommended explicit DWORD 1 is not in effect. Applying writes DWORD 1 and Undo restores the original kind and data.");
        }
        else
        {
            int current = Convert.ToInt32(value.Data, CultureInfo.InvariantCulture);
            assessment = current == EnabledValue
                ? OptimizationAssessment.Applied(
                    "On",
                    "AutoGameModeEnabled is explicitly set to 1, so Windows Game Mode is enabled.")
                : OptimizationAssessment.Available(
                    current == 0 ? "Off" : $"Off (unrecognized value {current})",
                    current == 0
                        ? "AutoGameModeEnabled is explicitly set to 0, so Windows Game Mode is disabled."
                        : $"AutoGameModeEnabled is {current}; the recommended value is 1. Applying writes 1.");
        }

        return Task.FromResult(assessment with { Evidence = [evidence] });
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
            EnabledValue);

        return Task.FromResult(OperationOutcome.Ok("Enabled Windows Game Mode.") with
        {
            Changes =
            [
                $@"{RegistryHive.CurrentUser}\{RegistrySubKey}\{ValueName} = {EnabledValue} (REG_DWORD)",
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

        return Task.FromResult(OperationOutcome.Ok("Restored the original Windows Game Mode setting.") with
        {
            Changes = original.Entries.Select(entry => entry.ToString()).ToList(),
        });
    }

    private static string Describe(RegistryValue? value)
    {
        if (value is null)
        {
            return "Not set (Windows default: on)";
        }

        if (value.Kind == RegistryValueKind.DWord)
        {
            int current = Convert.ToInt32(value.Data, CultureInfo.InvariantCulture);
            return current switch
            {
                EnabledValue => "1 (on)",
                0 => "0 (off)",
                _ => $"{current} (unrecognized; Windows treats anything other than 1 as not explicitly enabled)",
            };
        }

        return $"{RegistryValueCodec.Serialize(value.Data, value.Kind)} ({value.Kind})";
    }
}
