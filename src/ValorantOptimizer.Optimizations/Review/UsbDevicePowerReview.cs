using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Read-only USB device power overview. It counts enumerated USB devices, storage devices and host
/// controllers, but never disables a device or changes per-device power management.
/// </summary>
public sealed class UsbDevicePowerReview : IOptimization
{
    public const string OptimizationId = "usb-device-power";

    public const string EnumUsbKey = @"SYSTEM\CurrentControlSet\Enum\USB";
    public const string EnumUsbStorageKey = @"SYSTEM\CurrentControlSet\Enum\USBSTOR";

    /// <summary>USB host controllers and hubs class GUID.</summary>
    public const string UsbControllerClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{36fc9e60-c465-11cf-8056-444553540000}";

    private const string BlockedReason =
        "Per-device USB power management (\"Allow the computer to turn off this device to save power\") " +
        "is device- and driver-specific and is not automated here. Review individual devices in Device " +
        "Manager. The toolkit's global USB selective suspend card (usb-selective-suspend) handles the " +
        "system-wide power-plan setting; this card only reports counts.";

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = OptimizationId,
        Name = "USB device power (review)",
        Category = OptimizationCategory.Usb,
        Risk = RiskLevel.Safe,
        Summary = "Count USB devices, storage devices and host controllers; per-device power management is left to Device Manager.",
        WhatItChanges =
            @"Nothing. Counts subkeys under HKLM\SYSTEM\CurrentControlSet\Enum\USB, " +
            @"HKLM\SYSTEM\CurrentControlSet\Enum\USBSTOR and the USB controller class key. " +
            "No device, hub, driver or power setting is changed.",
        Why =
            "Windows can suspend individual USB devices to save power. Whether that is desirable depends " +
            "on the device: suspending an idle keyboard or storage drive is usually fine, while some " +
            "wireless receivers, audio interfaces and controllers may reconnect slowly. The right choice " +
            "is device-specific and is exposed per device in Device Manager, so the card shows the " +
            "inventory and leaves the setting to you. No input-latency improvement is promised.",
        Compatibility =
            "Windows 10 and Windows 11. Counts reflect the USB enumeration registry and may omit devices " +
            "that were not present when Windows last enumerated them.",
        StateWhenApplied = "Read-only review",
        Restart = RestartRequirement.None,
        RequiresElevation = false,
        ManagedScopes = [],
        Notes =
        [
            "Read-only: apply and restore are refused by design, and the card is disabled in the UI.",
            "No USB device is disabled, restarted or power-managed by this card.",
            "Per-device \"Allow the computer to turn off this device\" is configured in Device Manager.",
            "The global USB selective suspend policy is handled by the separate usb-selective-suspend card, not this one.",
        ],
    };

    public Task<OptimizationAssessment> AssessAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        UsbSnapshot snapshot = ReadSnapshot(context.Registry);

        var evidence = new List<AssessmentEvidence>
        {
            new("USB devices", CountText(snapshot.DeviceCount, snapshot.DevicesAvailable)),
            new("USB storage devices", CountText(snapshot.StorageDeviceCount, snapshot.StorageAvailable)),
            new("USB host controllers", CountText(snapshot.HostControllerCount, snapshot.ControllersAvailable)),
            new("Power management", "Per-device; review in Device Manager (global policy: usb-selective-suspend)"),
        };

        string headline = snapshot.DevicesAvailable && snapshot.ControllersAvailable
            ? $"USB overview: {snapshot.DeviceCount} device entr{(snapshot.DeviceCount == 1 ? "y" : "ies")}, " +
              $"{snapshot.HostControllerCount} host controller{(snapshot.HostControllerCount == 1 ? string.Empty : "s")}"
            : "USB overview is partially unavailable";

        OptimizationAssessment assessment = OptimizationAssessment.Review(BlockedReason, headline) with
        {
            Explanation =
                "The counts above come from the USB device enumeration registry. Suspending a specific " +
                "device is a device-level decision (Device Manager > device > Power Management) and is not " +
                "performed by this card. Nothing is changed by this card.",
            Evidence = evidence,
        };

        return Task.FromResult(assessment);
    }

    public Task<ChangeSet> CaptureAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        UsbSnapshot snapshot = ReadSnapshot(context.Registry);

        ChangeSet changeSet = ReviewOnlySupport.CreateChangeSet(
            Descriptor.Id,
            ("usbDeviceCount", snapshot.DevicesAvailable ? ReviewOnlySupport.Invariant(snapshot.DeviceCount) : "unavailable"),
            ("usbStorageDeviceCount", snapshot.StorageAvailable ? ReviewOnlySupport.Invariant(snapshot.StorageDeviceCount) : "unavailable"),
            ("usbHostControllerCount", snapshot.ControllersAvailable ? ReviewOnlySupport.Invariant(snapshot.HostControllerCount) : "unavailable"));

        return Task.FromResult(changeSet);
    }

    public Task<OperationOutcome> ApplyAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default) =>
        ReviewOnlySupport.RefuseAsync(context, original, cancellationToken);

    public Task<OperationOutcome> RestoreAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default) =>
        ReviewOnlySupport.RefuseAsync(context, original, cancellationToken);

    private static UsbSnapshot ReadSnapshot(IRegistryStore registry)
    {
        IReadOnlyList<string>? devices = ReviewOnlySupport.TryGetSubKeyNames(
            registry,
            RegistryHive.LocalMachine,
            EnumUsbKey);
        IReadOnlyList<string>? storage = ReviewOnlySupport.TryGetSubKeyNames(
            registry,
            RegistryHive.LocalMachine,
            EnumUsbStorageKey);
        IReadOnlyList<string>? controllers = ReviewOnlySupport.TryGetSubKeyNames(
            registry,
            RegistryHive.LocalMachine,
            UsbControllerClassKey);

        return new UsbSnapshot(
            devices?.Count ?? 0,
            storage?.Count ?? 0,
            controllers?.Count ?? 0,
            DevicesAvailable: devices is not null,
            StorageAvailable: storage is not null,
            ControllersAvailable: controllers is not null);
    }

    private static string CountText(int count, bool available) =>
        available ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "Unavailable";

    private sealed record UsbSnapshot(
        int DeviceCount,
        int StorageDeviceCount,
        int HostControllerCount,
        bool DevicesAvailable,
        bool StorageAvailable,
        bool ControllersAvailable);
}
