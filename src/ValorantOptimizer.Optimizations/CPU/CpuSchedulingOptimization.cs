using System.Globalization;
using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Sets the classic Windows "Foreground" CPU quantum profile (Win32PrioritySeparation = 38 /
/// 0x26) so foreground threads receive longer CPU slices than background threads.
/// </summary>
public sealed class CpuSchedulingOptimization : IOptimization
{
    public const string OptimizationId = "cpu-scheduling";
    public const string RegistrySubKey = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
    public const string ValueName = "Win32PrioritySeparation";

    /// <summary>
    /// 38 decimal = 0x26, the value the classic Performance Options "Foreground" choice writes.
    /// </summary>
    public const int RecommendedValue = 38;

    private static readonly RegistryTarget Target =
        new(RegistryHive.LocalMachine, RegistrySubKey, ValueName);

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = OptimizationId,
        Name = "CPU foreground scheduling quantum",
        Category = OptimizationCategory.CpuPower,
        Risk = RiskLevel.Experimental,
        Summary = "Sets Win32PrioritySeparation to 38 (0x26), the classic \"Foreground\" quantum profile that gives foreground threads a larger share of CPU time than background threads.",
        WhatItChanges = @"Writes the machine-wide DWORD HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl\Win32PrioritySeparation = 38 (0x26). Undo restores the exact original value. Requires administrator rights and a reboot.",
        Why = "Windows hands the CPU to runnable threads in quanta, and Win32PrioritySeparation controls quantum length, quantum variability and the foreground boost. Microsoft's classic documentation associates 0x26 with short, variable quanta and a 3:1 boost for the foreground process, which is why some players set it. Modern Windows schedulers are not publicly documented at this level, the tweak is disputed, and the effect ranges from subtle to none depending on the system; no frame-rate gain is promised.",
        Compatibility = "Windows 10 and Windows 11. Requires administrator rights and a reboot. This is a machine-wide scheduler policy, so background throughput can drop slightly while foreground responsiveness changes. Systems where power plans, firmware or anti-cheat already dominate scheduling may see no difference.",
        StateWhenApplied = "0x26 foreground quantum",
        Restart = RestartRequirement.Reboot,
        RequiresElevation = true,
        ManagedScopes = [new RegistryScope(RegistryHive.LocalMachine, RegistrySubKey)],
        Notes =
        [
            "38 decimal is 0x26, the value the classic Performance Options \"Foreground\" choice writes; Windows' client default is normally 2 (0x02), which resolves to OS defaults.",
            "Classic Microsoft documentation (Windows XP era) describes 0x26 as short, variable quanta with a 3:1 foreground boost; current Windows may interpret the value differently.",
            "This is a documented but disputed desktop tweak. It is not a guaranteed performance improvement and can make background work slightly slower.",
            "Experimental risk: apply only with the captured backup available so the exact previous value can be restored.",
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
                "Windows default quantum profile",
                "Win32PrioritySeparation is not set, so Windows uses its default quantum configuration. Applying writes 38 (0x26).");
        }
        else if (value.Kind != RegistryValueKind.DWord)
        {
            assessment = OptimizationAssessment.Available(
                "Windows default quantum profile (unexpected format)",
                $"Win32PrioritySeparation is stored as {value.Kind} instead of DWORD, so the recommended value 38 is not in effect. Applying writes DWORD 38 and Undo restores the original kind and data.");
        }
        else
        {
            int current = Convert.ToInt32(value.Data, CultureInfo.InvariantCulture);
            assessment = current == RecommendedValue
                ? OptimizationAssessment.Applied(
                    "0x26 foreground quantum",
                    "Win32PrioritySeparation is 38 (0x26), the classic foreground quantum profile. A reboot is required for the change to be fully in effect.")
                : current == 2
                    ? OptimizationAssessment.Available(
                        "Windows default quantum profile",
                        "Win32PrioritySeparation is 2 (0x02), the usual client default that resolves to the operating system's quantum defaults. Applying writes 38 (0x26).")
                    : OptimizationAssessment.Available(
                        $"Custom quantum profile ({current}, 0x{current:X})",
                        $"Win32PrioritySeparation is {current} (0x{current:X}) instead of the recommended 38 (0x26). Applying replaces it; Undo restores {current} exactly.");
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
            RecommendedValue);

        return Task.FromResult(OperationOutcome.Ok(
            "Set Win32PrioritySeparation to 38 (0x26), the classic foreground quantum profile. Reboot to apply it.") with
        {
            Changes =
            [
                $@"{RegistryHive.LocalMachine}\{RegistrySubKey}\{ValueName} = {RecommendedValue} (0x{RecommendedValue:X}, REG_DWORD)",
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
            "Restored the original CPU quantum configuration. Reboot for the change to take effect.") with
        {
            Changes = original.Entries.Select(entry => entry.ToString()).ToList(),
        });
    }

    private static string Describe(RegistryValue? value)
    {
        if (value is null)
        {
            return "Not set (Windows default quantum profile)";
        }

        if (value.Kind == RegistryValueKind.DWord)
        {
            int current = Convert.ToInt32(value.Data, CultureInfo.InvariantCulture);
            return current switch
            {
                RecommendedValue => $"{RecommendedValue} (0x{RecommendedValue:X}, foreground quantum profile)",
                2 => "2 (0x02, Windows default)",
                _ => $"{current} (0x{current:X})",
            };
        }

        return $"{RegistryValueCodec.Serialize(value.Data, value.Kind)} ({value.Kind})";
    }
}
