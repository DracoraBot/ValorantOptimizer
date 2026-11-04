using System.Globalization;
using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Disables Windows mouse acceleration ("Enhance pointer precision") by zeroing the three per-user
/// acceleration values and asking Windows to reload the mouse settings for the current session.
/// Pointer speed (<c>MouseSensitivity</c>) is deliberately left alone.
/// </summary>
public sealed class MouseAccelerationOptimization : IOptimization
{
    public const string OptimizationId = "mouse-acceleration";
    public const string RegistrySubKey = @"Control Panel\Mouse";
    public const string MouseSpeedValueName = "MouseSpeed";
    public const string MouseThreshold1ValueName = "MouseThreshold1";
    public const string MouseThreshold2ValueName = "MouseThreshold2";

    /// <summary>REG_SZ string Windows stores when acceleration is disabled.</summary>
    public const string DisabledValue = "0";

    private static readonly RegistryTarget[] Targets =
    [
        new(RegistryHive.CurrentUser, RegistrySubKey, MouseSpeedValueName),
        new(RegistryHive.CurrentUser, RegistrySubKey, MouseThreshold1ValueName),
        new(RegistryHive.CurrentUser, RegistrySubKey, MouseThreshold2ValueName),
    ];

    private readonly IMouseAccelerationApplier _applier;

    /// <summary>Creates the module with the real <c>SystemParametersInfo</c> applier.</summary>
    public MouseAccelerationOptimization()
        : this(new Win32MouseAccelerationApplier())
    {
    }

    /// <summary>Creates the module with an injected applier (tests pass a fake).</summary>
    public MouseAccelerationOptimization(IMouseAccelerationApplier applier)
    {
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
    }

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = OptimizationId,
        Name = "Disable mouse acceleration",
        Category = OptimizationCategory.Mouse,
        Risk = RiskLevel.Safe,
        Summary = "Turns off Windows mouse acceleration (\"Enhance pointer precision\") so equal mouse movement produces equal pointer movement.",
        WhatItChanges = @"Sets the REG_SZ values HKCU\Control Panel\Mouse\MouseSpeed, MouseThreshold1 and MouseThreshold2 to ""0"", then asks Windows to reload the mouse settings for the current session. Mouse pointer speed (MouseSensitivity) is not touched.",
        Why = "When pointer precision enhancement is on, Windows overlays an acceleration curve on top of the physical mouse movement, so the pointer can travel farther during fast flicks than during slow ones. Zeroing the speed and threshold values removes that curve, which gives consistent muscle memory on the Windows desktop and in titles that do not use raw input. Games using raw input may read the device directly and ignore this setting; no frame-rate change is promised.",
        Compatibility = "Windows 10 and Windows 11. The setting is per user and requires no administrator rights. It takes effect immediately for the desktop, and at the next sign-in for anything that cached the old values; games with raw input may ignore it entirely.",
        StateWhenApplied = "Acceleration off",
        Restart = RestartRequirement.None,
        RequiresElevation = false,
        ManagedScopes = [new RegistryScope(RegistryHive.CurrentUser, RegistrySubKey)],
        Notes =
        [
            "Mouse pointer speed (the Windows \"MouseSensitivity\" value) is deliberately not changed; this only removes the acceleration curve.",
            "VALORANT uses raw input by default, so the in-game sensitivity remains authoritative; disabling Windows acceleration mainly benefits desktop aim and games that use the Windows cursor.",
            "If the live input-settings refresh cannot be confirmed, the registry values still take effect after signing out and back in.",
        ],
    };

    public Task<OptimizationAssessment> AssessAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        RegistryValue? speed = Read(context, MouseSpeedValueName);
        RegistryValue? threshold1 = Read(context, MouseThreshold1ValueName);
        RegistryValue? threshold2 = Read(context, MouseThreshold2ValueName);

        var evidence = new List<AssessmentEvidence>
        {
            new(MouseSpeedValueName, Describe(speed)),
            new(MouseThreshold1ValueName, Describe(threshold1)),
            new(MouseThreshold2ValueName, Describe(threshold2)),
        };

        bool applied = IsDisabled(speed) && IsDisabled(threshold1) && IsDisabled(threshold2);

        OptimizationAssessment assessment;
        if (applied)
        {
            assessment = OptimizationAssessment.Applied(
                "Acceleration off",
                "MouseSpeed, MouseThreshold1 and MouseThreshold2 are all \"0\", so Windows pointer acceleration is disabled.");
        }
        else if (speed is null && threshold1 is null && threshold2 is null)
        {
            assessment = OptimizationAssessment.Available(
                "Acceleration on (Windows default)",
                "None of the three acceleration values is set, so Windows uses its built-in defaults, which enable pointer acceleration.");
        }
        else
        {
            assessment = OptimizationAssessment.Available(
                "Acceleration on",
                "At least one acceleration value is not \"0\", so Windows is still adding an acceleration curve to pointer movement.");
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
            Entries = RegistrySnapshot.CaptureMany(context.Registry, Targets),
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

        foreach (RegistryTarget target in Targets)
        {
            context.Registry.WriteValue(
                target.Hive,
                target.SubKey,
                target.ValueName,
                RegistryValueKind.String,
                DisabledValue);
        }

        bool liveSettingsApplied;
        try
        {
            liveSettingsApplied = _applier.TryApply(0, 0, 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            liveSettingsApplied = false;
        }

        var warnings = new List<string>();
        if (!liveSettingsApplied)
        {
            warnings.Add(
                "The registry values were changed, but Windows did not confirm the live input-settings refresh. " +
                "Sign out and back in to make sure mouse acceleration is off.");
        }

        return Task.FromResult(OperationOutcome.Ok("Disabled Windows mouse acceleration.") with
        {
            Changes = Targets
                .Select(target =>
                    $@"{target.Hive}\{target.SubKey}\{target.ValueName} = ""{DisabledValue}"" (REG_SZ)")
                .ToList(),
            Warnings = warnings,
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

        var warnings = new List<string>();
        if (!TryRefreshLiveSession(original))
        {
            warnings.Add(
                "The registry values were restored, but Windows did not confirm the live input-settings refresh. " +
                "Sign out and back in to make sure your original mouse behavior is active.");
        }

        return Task.FromResult(OperationOutcome.Ok("Restored the original Windows mouse acceleration settings.") with
        {
            Changes = original.Entries.Select(entry => entry.ToString()).ToList(),
            Warnings = warnings,
        });
    }

    private bool TryRefreshLiveSession(ChangeSet original)
    {
        int speed = ReadRestored(original, "MouseSpeed", fallback: 1);
        int threshold1 = ReadRestored(original, "MouseThreshold1", fallback: 6);
        int threshold2 = ReadRestored(original, "MouseThreshold2", fallback: 10);

        try
        {
            return _applier.TryApply(speed, threshold1, threshold2);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int ReadRestored(ChangeSet original, string valueName, int fallback)
    {
        RegistrySnapshotEntry? entry = original.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.ValueName, valueName, StringComparison.OrdinalIgnoreCase));
        if (entry is null || !entry.Existed)
        {
            return fallback;
        }

        return int.TryParse(entry.SerializedData, out int value) && value >= 0 ? value : fallback;
    }

    private static RegistryValue? Read(IOptimizationContext context, string valueName) =>
        context.Registry.ReadValue(RegistryHive.CurrentUser, RegistrySubKey, valueName);

    private static bool IsDisabled(RegistryValue? value) =>
        value is not null &&
        value.Kind == RegistryValueKind.String &&
        string.Equals(
            Convert.ToString(value.Data, CultureInfo.InvariantCulture),
            DisabledValue,
            StringComparison.Ordinal);

    private static string Describe(RegistryValue? value)
    {
        if (value is null)
        {
            return "Not set (Windows default: acceleration on)";
        }

        string data = RegistryValueCodec.Serialize(value.Data, value.Kind);
        return value.Kind == RegistryValueKind.String ? data : $"{data} ({value.Kind})";
    }
}
