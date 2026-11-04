using System.Globalization;
using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Disables Windows Power Throttling machine-wide. Power Throttling lets Windows move work it
/// considers background onto lower-power CPU states; turning it off trades battery life and heat
/// for less aggressive power-based throttling.
/// </summary>
public sealed class CpuPowerThrottlingOptimization : IOptimization
{
    public const string OptimizationId = "cpu-power-throttling";
    public const string RegistrySubKey = @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling";
    public const string ValueName = "PowerThrottlingOff";

    private const int ThrottlingDisabledValue = 1;

    private static readonly RegistryTarget Target =
        new(RegistryHive.LocalMachine, RegistrySubKey, ValueName);

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = OptimizationId,
        Name = "Disable CPU power throttling",
        Category = OptimizationCategory.CpuPower,
        Risk = RiskLevel.Advanced,
        Summary = "Turns off Windows Power Throttling system-wide so the power policy stops moving throttled work onto lower-power CPU states.",
        WhatItChanges = @"Writes the machine-wide DWORD HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling\PowerThrottlingOff = 1. Undo restores the previous value, or deletes it when it did not exist. Requires administrator rights and a reboot.",
        Why = "Power Throttling is a Windows power-management feature that can lower CPU frequency or keep work on efficiency-oriented cores for processes Windows classifies as background, in order to save battery. Disabling it removes that specific power policy, which can help sustained CPU-heavy work on some laptops, but it also increases power draw, heat and fan noise. It is a system-wide power tradeoff, not a validated frame-rate improvement.",
        Compatibility = "Windows 10 and Windows 11, desktop and laptop. Requires administrator rights and a reboot. Most useful on desktops or plugged-in laptops with adequate cooling; on battery, expect reduced runtime and higher temperatures. OEM firmware and other power settings can still throttle the CPU independently.",
        StateWhenApplied = "Power throttling off",
        Restart = RestartRequirement.Reboot,
        RequiresElevation = true,
        ManagedScopes = [new RegistryScope(RegistryHive.LocalMachine, RegistrySubKey)],
        Notes =
        [
            "This is machine-wide, not per app: the power policy change applies to every process on the PC.",
            "Disabling power throttling can increase power draw, heat and fan noise; on battery it reduces runtime.",
            "A reboot is required before the change is fully in effect.",
            "Firmware thermal limits, power plans and OEM utilities can still throttle the CPU; this module does not override them.",
        ],
    };

    public Task<OptimizationAssessment> AssessAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        RegistryValue? value = context.Registry.ReadValue(
            RegistryHive.LocalMachine,
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
                "Power throttling on (Windows default)",
                "PowerThrottlingOff is not set, so Windows Power Throttling uses its default behavior, which is enabled.");
        }
        else if (value.Kind != RegistryValueKind.DWord)
        {
            assessment = OptimizationAssessment.Available(
                "Power throttling on (unexpected format)",
                $"PowerThrottlingOff is stored as {value.Kind} instead of DWORD, so the recommended value 1 is not in effect. Applying writes DWORD 1 and Undo restores the original kind and data.");
        }
        else
        {
            int current = Convert.ToInt32(value.Data, CultureInfo.InvariantCulture);
            assessment = current == ThrottlingDisabledValue
                ? OptimizationAssessment.Applied(
                    "Power throttling off",
                    "PowerThrottlingOff is 1, so Windows Power Throttling is disabled; a reboot is required for the change to be fully in effect.")
                : OptimizationAssessment.Available(
                    "Power throttling on",
                    $"PowerThrottlingOff is {current} (0 means power throttling is enabled). Applying sets it to 1; a reboot is required.");
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
            RegistryHive.LocalMachine,
            RegistrySubKey,
            ValueName,
            RegistryValueKind.DWord,
            ThrottlingDisabledValue);

        return Task.FromResult(OperationOutcome.Ok(
            "Disabled Windows Power Throttling. Reboot for the change to take effect.") with
        {
            Changes =
            [
                $@"{RegistryHive.LocalMachine}\{RegistrySubKey}\{ValueName} = {ThrottlingDisabledValue} (REG_DWORD)",
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
            "Restored the original Power Throttling setting. Reboot for the change to take effect.") with
        {
            Changes = original.Entries.Select(entry => entry.ToString()).ToList(),
        });
    }

    private static string Describe(RegistryValue? value)
    {
        if (value is null)
        {
            return "Not set (Windows default: power throttling enabled)";
        }

        if (value.Kind == RegistryValueKind.DWord)
        {
            int current = Convert.ToInt32(value.Data, CultureInfo.InvariantCulture);
            return current switch
            {
                ThrottlingDisabledValue => "1 (power throttling off)",
                0 => "0 (power throttling on)",
                _ => $"{current} (unrecognized; only 1 disables throttling)",
            };
        }

        return $"{RegistryValueCodec.Serialize(value.Data, value.Kind)} ({value.Kind})";
    }
}
