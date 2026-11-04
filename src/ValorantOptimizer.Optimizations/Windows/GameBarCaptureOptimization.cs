using System.Globalization;
using Microsoft.Win32;
using ValorantOptimizer.Core;

namespace ValorantOptimizer.Optimizations;

/// <summary>
/// Disables the two per-user values that keep Xbox Game Bar background capture (Game DVR) running.
/// Missing values mean Windows defaults background capture to on, so they count as not applied.
/// </summary>
public sealed class GameBarCaptureOptimization : IOptimization
{
    public const string OptimizationId = "game-bar-capture";
    public const string GameDvrSubKey = @"System\GameConfigStore";
    public const string GameDvrValueName = "GameDVR_Enabled";
    public const string AppCaptureSubKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    public const string AppCaptureValueName = "AppCaptureEnabled";

    private const int DisabledValue = 0;

    private static readonly RegistryTarget[] Targets =
    [
        new(RegistryHive.CurrentUser, GameDvrSubKey, GameDvrValueName),
        new(RegistryHive.CurrentUser, AppCaptureSubKey, AppCaptureValueName),
    ];

    public OptimizationDescriptor Descriptor { get; } = new()
    {
        Id = OptimizationId,
        Name = "Xbox Game Bar background capture",
        Category = OptimizationCategory.Windows,
        Risk = RiskLevel.Safe,
        Summary = "Turns off Game Bar's background game capture so clip recording is not always running while you play.",
        WhatItChanges = @"Writes DWORD 0 to HKCU\System\GameConfigStore\GameDVR_Enabled and to HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR\AppCaptureEnabled. Undo restores each previous value, or deletes values that did not exist.",
        Why = "When background capture (Game DVR) is on, Windows keeps a capture pipeline ready and can buffer recent gameplay, which may use some GPU, CPU and disk time. Turning the background path off stops that work; the actual cost on modern hardware can be small, and no frame-rate gain is promised. Xbox Game Bar is still installed and can still be opened manually.",
        Compatibility = "Windows 10 and Windows 11. Per-user settings; no administrator rights are required. Some systems do not have the Xbox Game Bar app installed; the registry values are then inert. A Group Policy or the Windows 11 \"Captures\" settings can override them.",
        StateWhenApplied = "Background capture off",
        Restart = RestartRequirement.None,
        RequiresElevation = false,
        ManagedScopes =
        [
            new RegistryScope(RegistryHive.CurrentUser, GameDvrSubKey),
            new RegistryScope(RegistryHive.CurrentUser, AppCaptureSubKey),
        ],
        Notes =
        [
            "Xbox Game Bar can still be launched manually with Win+G after this change, and it can still record when you start a capture yourself.",
            "Windows 11 exposes related options under Settings > Gaming > Captures; changing them there can override these values.",
            "A missing value means Windows uses its default, which leaves background capture enabled, so the module reports those values as not applied.",
        ],
    };

    public Task<OptimizationAssessment> AssessAsync(
        IOptimizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        RegistryValue? gameDvr = Read(context, GameDvrSubKey, GameDvrValueName);
        RegistryValue? appCapture = Read(context, AppCaptureSubKey, AppCaptureValueName);

        bool gameDvrOff = IsDisabled(gameDvr);
        bool appCaptureOff = IsDisabled(appCapture);

        var evidence = new List<AssessmentEvidence>
        {
            new(GameDvrValueName, Describe(gameDvr)),
            new(AppCaptureValueName, Describe(appCapture)),
        };

        OptimizationAssessment assessment;
        if (gameDvrOff && appCaptureOff)
        {
            assessment = OptimizationAssessment.Applied(
                "Background capture off",
                "GameDVR_Enabled and AppCaptureEnabled are both 0, so Game Bar background capture is disabled.");
        }
        else if (gameDvr is null && appCapture is null)
        {
            assessment = OptimizationAssessment.Available(
                "Background capture on (Windows default)",
                "Neither value is set, so Windows keeps its default behavior, which enables Game Bar background capture.");
        }
        else if (gameDvrOff || appCaptureOff)
        {
            assessment = OptimizationAssessment.Available(
                "Background capture partially disabled",
                "One of the two background-capture values is already 0 while the other is still enabled or missing (Windows defaults it to on). Applying sets both to 0.");
        }
        else
        {
            assessment = OptimizationAssessment.Available(
                "Background capture on",
                "GameDVR_Enabled and/or AppCaptureEnabled are set to a non-zero value, so Game Bar background capture is enabled.");
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
                RegistryValueKind.DWord,
                DisabledValue);
        }

        return Task.FromResult(OperationOutcome.Ok("Disabled Xbox Game Bar background capture.") with
        {
            Changes = Targets
                .Select(target =>
                    $@"{target.Hive}\{target.SubKey}\{target.ValueName} = {DisabledValue} (REG_DWORD)")
                .ToList(),
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

        return Task.FromResult(OperationOutcome.Ok("Restored the original Xbox Game Bar capture settings.") with
        {
            Changes = original.Entries.Select(entry => entry.ToString()).ToList(),
        });
    }

    private static RegistryValue? Read(IOptimizationContext context, string subKey, string valueName) =>
        context.Registry.ReadValue(RegistryHive.CurrentUser, subKey, valueName);

    private static bool IsDisabled(RegistryValue? value) =>
        value is not null &&
        value.Kind == RegistryValueKind.DWord &&
        Convert.ToInt32(value.Data, CultureInfo.InvariantCulture) == DisabledValue;

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
                DisabledValue => "0 (off)",
                1 => "1 (on)",
                _ => $"{current} (on; non-zero)",
            };
        }

        return $"{RegistryValueCodec.Serialize(value.Data, value.Kind)} ({value.Kind})";
    }
}
