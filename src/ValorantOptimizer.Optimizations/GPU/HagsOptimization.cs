using System.Globalization;
using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Enables hardware-accelerated GPU scheduling (HAGS) through HwSchMode = 2. HAGS is available from
/// Windows 10 version 2004 (build 19041) and depends on GPU/driver support; older builds are
/// reported as unsupported instead of writing the value.
/// </summary>
public sealed class HagsOptimization : IOptimization
{
    public const string OptimizationId = "gpu-hags";
    public const string RegistrySubKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    public const string ValueName = "HwSchMode";
    public const string WindowsVersionSubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    public const string BuildNumberValueName = "CurrentBuildNumber";

    /// <summary>HAGS first shipped with Windows 10 version 2004 (build 19041).</summary>
    public const int MinimumBuild = 19041;

    public const int EnabledMode = 2;
    public const int DisabledMode = 1;

    private static readonly RegistryTarget Target =
        new(RegistryHive.LocalMachine, RegistrySubKey, ValueName);

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = OptimizationId,
        Name = "Hardware-accelerated GPU scheduling",
        Category = OptimizationCategory.Gpu,
        Risk = RiskLevel.Advanced,
        Summary = "Enables hardware-accelerated GPU scheduling so the GPU manages its own command and memory scheduling instead of the CPU-side scheduler.",
        WhatItChanges = @"Writes the machine-wide DWORD HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode = 2 (2 = enabled, 1 = disabled). Undo restores the previous value, or deletes it when it did not exist. Requires administrator rights and a reboot.",
        Why = "With hardware-accelerated GPU scheduling, the GPU's scheduling processor manages command submission and memory residency instead of relying on the CPU-side scheduler. On supported GPUs and drivers this can reduce CPU overhead and improve frame pacing in some titles; on other combinations the difference is small or it can cause stutter or driver issues. It is hardware/driver dependent and requires a reboot.",
        Compatibility = "Windows 10 version 2004 (build 19041) or newer with a GPU and driver that expose HAGS. Older Windows builds are reported Unsupported. Laptops with hybrid graphics, older drivers, virtual machines and some OEM configurations may not support it even when the setting exists.",
        StateWhenApplied = "HAGS on",
        Restart = RestartRequirement.Reboot,
        RequiresElevation = true,
        ManagedScopes = [new RegistryScope(RegistryHive.LocalMachine, RegistrySubKey)],
        Notes =
        [
            "Requires Windows build 19041 or newer; the module reports Unsupported on older builds and does not write anything.",
            "Both the GPU and the installed driver must support HAGS; Windows may accept the value but ignore it on unsupported adapters.",
            "A reboot is required before the change is in effect. If the system becomes unstable or stutters after enabling, use Undo and reboot.",
            "No frame-rate improvement is guaranteed; the result depends on the game, GPU, driver and display configuration.",
        ],
    };

    public Task<OptimizationAssessment> AssessAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        BuildInfo build = ReadBuild(context.Registry);
        var evidence = new List<AssessmentEvidence>
        {
            new("Windows build", build.Description),
        };

        if (build.Number is null)
        {
            return Task.FromResult(OptimizationAssessment.Unsupported(
                "The Windows build number could not be determined, so hardware-accelerated GPU scheduling support cannot be verified.",
                "Windows build not available") with
            {
                Evidence = evidence,
            });
        }

        if (build.Number < MinimumBuild)
        {
            return Task.FromResult(OptimizationAssessment.Unsupported(
                $"Hardware-accelerated GPU scheduling requires Windows 10 version 2004 (build {MinimumBuild}) or newer; this PC reports build {build.Number}.",
                "Requires Windows 10 2004 or newer") with
            {
                Evidence = evidence,
            });
        }

        RegistryValue? value = context.Registry.ReadValue(
            RegistryHive.LocalMachine,
            RegistrySubKey,
            ValueName);
        evidence.Add(new AssessmentEvidence(ValueName, Describe(value)));

        OptimizationAssessment assessment;
        if (value is null)
        {
            assessment = OptimizationAssessment.Available(
                "HAGS not configured (Windows/driver default)",
                "HwSchMode is not set, so Windows and the GPU driver choose the default scheduling mode. Applying writes 2 (enabled) and requires a reboot.");
        }
        else if (value.Kind != RegistryValueKind.DWord)
        {
            assessment = OptimizationAssessment.Available(
                "HAGS not configured (unexpected format)",
                $"HwSchMode is stored as {value.Kind} instead of DWORD, so the recommended value 2 is not in effect. Applying writes DWORD 2 and Undo restores the original kind and data.");
        }
        else
        {
            int current = Convert.ToInt32(value.Data, CultureInfo.InvariantCulture);
            assessment = current switch
            {
                EnabledMode => OptimizationAssessment.Applied(
                    "HAGS on",
                    "HwSchMode is 2, so hardware-accelerated GPU scheduling is enabled; a reboot is required if it was just changed."),
                DisabledMode => OptimizationAssessment.Available(
                    "HAGS off",
                    "HwSchMode is 1, so hardware-accelerated GPU scheduling is disabled. Applying writes 2 and requires a reboot."),
                _ => OptimizationAssessment.Available(
                    $"HAGS in an unrecognized mode ({current})",
                    $"HwSchMode is {current} instead of 1 (disabled) or 2 (enabled). Applying writes 2; Undo restores {current} exactly."),
            };
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

        // Re-check at apply time so a stale assessment can never write HAGS on an unsupported build.
        BuildInfo build = ReadBuild(context.Registry);
        if (build.Number is null)
        {
            return Task.FromResult(OperationOutcome.Failed(
                "The Windows build number could not be determined, so HAGS was not enabled."));
        }

        if (build.Number < MinimumBuild)
        {
            return Task.FromResult(OperationOutcome.Failed(
                $"Hardware-accelerated GPU scheduling requires Windows build {MinimumBuild} or newer; this PC reports {build.Number}, so nothing was changed."));
        }

        context.Registry.WriteValue(
            RegistryHive.LocalMachine,
            RegistrySubKey,
            ValueName,
            RegistryValueKind.DWord,
            EnabledMode);

        return Task.FromResult(OperationOutcome.Ok(
            "Enabled hardware-accelerated GPU scheduling. Reboot for the change to take effect.") with
        {
            Changes =
            [
                $@"{RegistryHive.LocalMachine}\{RegistrySubKey}\{ValueName} = {EnabledMode} (REG_DWORD, enabled)",
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
            "Restored the original hardware-accelerated GPU scheduling setting. Reboot for the change to take effect.") with
        {
            Changes = original.Entries.Select(entry => entry.ToString()).ToList(),
        });
    }

    private static BuildInfo ReadBuild(IRegistryStore registry)
    {
        RegistryValue? value = registry.ReadValue(
            RegistryHive.LocalMachine,
            WindowsVersionSubKey,
            BuildNumberValueName);

        if (value is null)
        {
            return new BuildInfo(null, "Not available");
        }

        string text = Convert.ToString(value.Data, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        bool parsed = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number);
        return new BuildInfo(
            parsed ? number : null,
            parsed ? number.ToString(CultureInfo.InvariantCulture) : $"'{text}' (not a number)");
    }

    private static string Describe(RegistryValue? value)
    {
        if (value is null)
        {
            return "Not set (Windows/driver default)";
        }

        if (value.Kind == RegistryValueKind.DWord)
        {
            int current = Convert.ToInt32(value.Data, CultureInfo.InvariantCulture);
            return current switch
            {
                EnabledMode => "2 (enabled)",
                DisabledMode => "1 (disabled)",
                _ => $"{current} (unrecognized mode)",
            };
        }

        return $"{RegistryValueCodec.Serialize(value.Data, value.Kind)} ({value.Kind})";
    }

    private readonly record struct BuildInfo(int? Number, string Description);
}
