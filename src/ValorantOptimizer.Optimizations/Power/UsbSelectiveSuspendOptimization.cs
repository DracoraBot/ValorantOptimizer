using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Disables USB selective suspend (sets the active power plan's AC value to 0) so Windows does not
/// power down idle USB devices. The DC (battery) value is left alone. Changes are made only through
/// <c>powercfg.exe</c>.
/// </summary>
public sealed class UsbSelectiveSuspendOptimization : PowerSchemeSettingOptimizationBase
{
    internal const string UsbSubgroupGuid = "2a737441-1930-4402-8d77-b2bebba308a3";
    internal const string SelectiveSuspendSettingGuid = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    internal const uint RecommendedSelectiveSuspendIndex = 0;

    protected override string SubgroupGuid => UsbSubgroupGuid;

    protected override string SettingGuid => SelectiveSuspendSettingGuid;

    protected override uint RecommendedAcValue => RecommendedSelectiveSuspendIndex;

    protected override string SettingDisplayName => "USB selective suspend";

    protected override string AppliedHeadline => "USB selective suspend is disabled (AC)";

    protected override string NotAppliedHeadline => "Windows may suspend idle USB devices (AC)";

    protected override string AssessmentExplanation =>
        "USB selective suspend lets Windows power down idle USB devices to save energy. A value of 0 " +
        "disables it on the active power plan's AC (plugged-in) profile, keeping devices powered. " +
        "Applying it changes power behavior system-wide, increases idle USB power use, and is hardware " +
        "and driver dependent. No reboot is required and no input-latency or frame-rate gain is promised.";

    protected override string ApplySuccessMessage =>
        "Disabled USB selective suspend on the active power plan's AC setting and reactivated the plan. " +
        "This changes power behavior system-wide; no reboot is required. Some devices may need to be " +
        "re-plugged before the change is observed.";

    public override OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = "usb-selective-suspend",
        Name = "Disable USB selective suspend",
        Category = OptimizationCategory.Usb,
        Risk = RiskLevel.Advanced,
        Summary = "Stop Windows from suspending idle USB devices on AC power.",
        WhatItChanges =
            "Writes the active Windows power plan's AC (plugged-in) value for \"USB selective suspend " +
            "setting\" (subgroup 2a737441-1930-4402-8d77-b2bebba308a3, setting " +
            "48e6b7a6-50f5-4782-a5d4-53bb8f07e226) to 0 (Disabled) via powercfg /setacvalueindex, then " +
            "reactivates the plan. No USB driver, device or DC/battery value is changed.",
        Why =
            "Selective suspend powers down idle USB devices. Disabling it keeps controllers and " +
            "high-polling peripherals awake, which can avoid wake/re-enumeration stalls on some systems. " +
            "It increases idle USB power use and the result is hardware/driver dependent; no input-latency " +
            "or frame-rate gain is promised.",
        Compatibility =
            "Windows 10/11 with the USB power subgroup exposed. Some USB controllers and drivers ignore " +
            "the setting; when it cannot be read the module reports Unsupported instead of guessing.",
        StateWhenApplied = "USB selective suspend off (AC)",
        Restart = RestartRequirement.None,
        RequiresElevation = true,
        ManagedScopes = [],
        Notes =
        [
            "Changes the active power plan's AC (plugged-in) USB setting system-wide; the DC/battery value is left alone.",
            "USB devices stay powered and may use more energy, including while on battery if the DC value is enabled separately.",
            "No reboot is required, though some USB devices may need to be re-plugged before the change is observed.",
            "Undo restores the exact captured AC value and the original power plan; it requires the saved backup.",
            "No input-latency or FPS improvement is guaranteed.",
        ],
    };

    protected override string FormatValue(uint value) => value switch
    {
        0 => "0 (Disabled, 0x00000000)",
        1 => "1 (Enabled, 0x00000001)",
        _ => $"{value} (unrecognized, 0x{value:X8})",
    };
}
