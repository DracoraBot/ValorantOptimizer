using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Sets the active power plan's AC value for "Processor performance core parking min cores" to
/// 100% so Windows keeps every logical processor available. The value is a percentage of logical
/// cores; DC (battery) settings are left alone. Changes are made only through <c>powercfg.exe</c>.
/// </summary>
public sealed class CpuCoreParkingOptimization : PowerSchemeSettingOptimizationBase
{
    internal const string ProcessorSubgroupGuid = "54533251-82be-4824-96c1-47b60b740d00";
    internal const string CoreParkingMinimumCoresSettingGuid = "0cc5b647-c1df-4637-891a-dec35c318583";
    internal const uint RecommendedCoreParkingPercent = 100;

    protected override string SubgroupGuid => ProcessorSubgroupGuid;

    protected override string SettingGuid => CoreParkingMinimumCoresSettingGuid;

    protected override uint RecommendedAcValue => RecommendedCoreParkingPercent;

    protected override string SettingDisplayName => "Core parking minimum cores";

    protected override string AppliedHeadline => "All logical CPU cores are kept available (AC)";

    protected override string NotAppliedHeadline => "Windows may park logical CPU cores (AC)";

    protected override string AssessmentExplanation =>
        "Core parking lets Windows park logical processors when it expects low load. The setting is a " +
        "percentage of logical cores in the active power plan's AC (plugged-in) profile; forcing 100% " +
        "keeps every logical core available. Applying it changes processor behavior system-wide, can " +
        "increase power use and heat, and is hardware/workload dependent. No reboot is required and no " +
        "frame-rate gain is promised.";

    protected override string ApplySuccessMessage =>
        "Set the active power plan's AC core parking minimum to 100% and reactivated the plan. This " +
        "changes processor behavior system-wide; no reboot is required. The effect on frame rate is " +
        "hardware-dependent.";

    public override OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = "cpu-core-parking",
        Name = "Keep all CPU cores unparked",
        Category = OptimizationCategory.CpuPower,
        Risk = RiskLevel.Advanced,
        Summary = "Set the current power plan's minimum unparked CPU cores to 100% on AC power.",
        WhatItChanges =
            "Writes the active Windows power plan's AC (plugged-in) value for \"Processor performance " +
            "core parking min cores\" (subgroup 54533251-82be-4824-96c1-47b60b740d00, setting " +
            "0cc5b647-c1df-4637-891a-dec35c318583) to 100 via powercfg /setacvalueindex, then reactivates " +
            "the plan. No CPU affinity, overclock, driver or DC/battery value is changed.",
        Why =
            "Core parking lets Windows remove logical processors from the scheduler when it predicts low " +
            "load. Requiring a 100% minimum keeps every logical core available, which can reduce scheduling " +
            "wake-up latency on some CPUs. The effect depends on the processor, Windows build and workload; " +
            "no frame-rate gain is promised.",
        Compatibility =
            "Windows 10/11 with the Processor power management subgroup exposed by the CPU driver. " +
            "Availability varies by processor and power plan; when the setting cannot be read the module " +
            "reports Unsupported instead of guessing.",
        StateWhenApplied = "All cores unparked (AC)",
        Restart = RestartRequirement.None,
        RequiresElevation = true,
        ManagedScopes = [],
        Notes =
        [
            "Changes the active power plan's AC (plugged-in) processor setting system-wide; the DC/battery value is left alone.",
            "Keeping cores ready can increase power use, heat and fan noise, especially on laptops.",
            "No reboot is required, though some Windows builds apply processor settings only after the plan is reactivated.",
            "Undo restores the exact captured AC value and the original power plan; it requires the saved backup.",
            "No FPS or latency improvement is guaranteed.",
        ],
    };

    protected override string FormatValue(uint value) => $"{value}% (0x{value:X8})";
}
