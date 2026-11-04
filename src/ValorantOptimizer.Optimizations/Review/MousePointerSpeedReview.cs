using System.Globalization;
using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Read-only review of the Windows pointer-speed slider. The pointer speed is a personal
/// preference that depends on the mouse, DPI and the player's in-game sensitivity, so the module
/// reports the stored value and refuses to change it automatically.
/// </summary>
public sealed class MousePointerSpeedReview : IOptimization
{
    public const string OptimizationId = "mouse-pointer-speed";
    public const string RegistrySubKey = @"Control Panel\Mouse";
    public const string ValueName = "MouseSensitivity";

    /// <summary>Windows' default position for the pointer-speed slider.</summary>
    public const int WindowsDefaultPointerSpeed = 10;

    public const int MinimumPointerSpeed = 1;
    public const int MaximumPointerSpeed = 20;

    private const string BlockedReason =
        "Pointer speed is a personal preference, not a system optimization: the right value depends " +
        "on your mouse DPI, display scaling, desk space and in-game sensitivity. The toolkit never " +
        "changes MouseSensitivity automatically; adjust it in Windows Settings > Bluetooth & devices > " +
        "Mouse, or use VALORANT's in-game sensitivity instead.";

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = OptimizationId,
        Name = "Mouse pointer speed (review)",
        Category = OptimizationCategory.Mouse,
        Risk = RiskLevel.Safe,
        Summary = "Review the Windows pointer-speed slider without changing it; pointer speed is a personal preference, not a system optimization.",
        WhatItChanges =
            @"Nothing. This read-only card reports the stored REG_SZ value " +
            @"HKCU\Control Panel\Mouse\MouseSensitivity (1-20) and the Windows default of 10. " +
            "It never writes the registry or asks Windows to reload input settings.",
        Why =
            "Pointer speed changes how far the cursor travels for a given physical mouse movement. " +
            "The comfortable value depends on the mouse DPI, display scaling, available desk space and " +
            "the in-game sensitivity. VALORANT uses raw input by default, which makes the in-game " +
            "sensitivity authoritative; this card therefore explains the Windows value instead of " +
            "choosing one for you. No frame-rate change is promised.",
        Compatibility =
            "Windows 10 and Windows 11. MouseSensitivity is a per-user HKCU value and reading it " +
            "requires no administrator rights. Custom input stacks or third-party mouse drivers may " +
            "apply their own speed curve on top of this slider.",
        StateWhenApplied = "Read-only review",
        Restart = RestartRequirement.None,
        RequiresElevation = false,
        ManagedScopes = [],
        Notes =
        [
            "Read-only: apply and restore are refused by design, and the card is disabled in the UI.",
            "Windows stores this value as REG_SZ even though it is conceptually a number from 1 to 20.",
            "VALORANT uses raw input by default, so configure the sensitivity you aim with in the game first.",
            "The Windows default position is 10; custom mouse software can override the effective speed.",
        ],
    };

    public Task<OptimizationAssessment> AssessAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        RegistryValue? value = ReviewOnlySupport.TryReadValue(
            context.Registry,
            RegistryHive.CurrentUser,
            RegistrySubKey,
            ValueName);

        bool recognized = TryGetPointerSpeed(value, out int pointerSpeed);

        var evidence = new List<AssessmentEvidence>
        {
            new("Pointer speed", Describe(value, recognized, pointerSpeed)),
            new("Windows default", WindowsDefaultPointerSpeed.ToString(CultureInfo.InvariantCulture)),
        };

        string headline;
        string explanation;
        if (value is null)
        {
            headline = $"Pointer speed uses the Windows default ({WindowsDefaultPointerSpeed})";
            explanation =
                "MouseSensitivity is not stored for this user, so Windows uses the default slider " +
                $"position of {WindowsDefaultPointerSpeed} (of 1-20). Nothing is changed by this card.";
        }
        else if (recognized)
        {
            headline = $"Pointer speed is {pointerSpeed} of {MaximumPointerSpeed} (Windows default {WindowsDefaultPointerSpeed})";
            explanation =
                $"MouseSensitivity is stored as \"{pointerSpeed}\" (of 1-20), which is a preference rather " +
                "than an optimization. Windows and games that do not use raw input apply this speed; " +
                "VALORANT uses raw input by default, so its in-game sensitivity stays authoritative. " +
                "Nothing is changed by this card.";
        }
        else
        {
            headline = "Pointer speed is stored in a non-standard format";
            explanation =
                "MouseSensitivity is present but is not a plain number between 1 and 20, so the card " +
                "does not guess at a value. Windows may still render a slider position; review it in " +
                "Windows Settings. Nothing is changed by this card.";
        }

        OptimizationAssessment assessment = OptimizationAssessment.Review(BlockedReason, headline) with
        {
            Explanation = explanation,
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

        RegistryValue? value = ReviewOnlySupport.TryReadValue(
            context.Registry,
            RegistryHive.CurrentUser,
            RegistrySubKey,
            ValueName);

        string encoded = value is null
            ? "not set"
            : RegistryValueCodec.Serialize(value.Data, value.Kind);

        ChangeSet changeSet = ReviewOnlySupport.CreateChangeSet(
            Descriptor.Id,
            ("pointerSpeed", encoded),
            ("pointerSpeedKind", value?.Kind.ToString() ?? "absent"),
            ("windowsDefault", WindowsDefaultPointerSpeed.ToString(CultureInfo.InvariantCulture)));

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

    private static bool TryGetPointerSpeed(RegistryValue? value, out int speed)
    {
        speed = 0;
        if (value is null || value.Kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
        {
            return false;
        }

        string text = Convert.ToString(value.Data, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out speed)
               && speed is >= MinimumPointerSpeed and <= MaximumPointerSpeed;
    }

    private static string Describe(RegistryValue? value, bool recognized, int pointerSpeed)
    {
        if (value is null)
        {
            return $"Not set (Windows default: {WindowsDefaultPointerSpeed})";
        }

        if (value.Kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
        {
            string encoded = RegistryValueCodec.Serialize(value.Data, value.Kind);
            return $"{encoded} ({value.Kind}; Windows default: {WindowsDefaultPointerSpeed})";
        }

        string raw = Convert.ToString(value.Data, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        return recognized
            ? $"{pointerSpeed} (of {MinimumPointerSpeed}-{MaximumPointerSpeed})"
            : $"\"{raw}\" (not a number from {MinimumPointerSpeed} to {MaximumPointerSpeed}; Windows default: {WindowsDefaultPointerSpeed})";
    }
}
