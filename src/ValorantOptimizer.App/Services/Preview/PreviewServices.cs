using Microsoft.Win32;
using ValorantOptimizer.Core;
using ValorantOptimizer.Diagnostics;

namespace ValorantOptimizer.App.Services.Preview;

/// <summary>
/// In-memory services used by the hidden `--demo` UI preview. They never read or write the real
/// system and are replaced by the real composition root in normal runs.
/// </summary>
internal sealed class PreviewOptimization : IOptimization
{
    private readonly IReadOnlyList<AssessmentEvidence> _evidence;
    private OptimizationState _state;
    private string _headline;
    private string? _explanation;
    private string? _blockedReason;

    public PreviewOptimization(
        OptimizationDescriptor descriptor,
        OptimizationState state,
        string headline,
        string? explanation = null,
        IReadOnlyList<AssessmentEvidence>? evidence = null,
        string? blockedReason = null)
    {
        Descriptor = descriptor;
        _state = state;
        _headline = headline;
        _explanation = explanation;
        _evidence = evidence ?? [];
        _blockedReason = blockedReason;
    }

    public OptimizationDescriptor Descriptor { get; }

    public Task<OptimizationAssessment> AssessAsync(IOptimizationContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new OptimizationAssessment
        {
            State = _state,
            Headline = _headline,
            Explanation = _explanation,
            Evidence = [.. _evidence],
            BlockedReason = _blockedReason,
        });

    public Task<ChangeSet> CaptureAsync(IOptimizationContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChangeSet
        {
            OptimizationId = Descriptor.Id,
            Metadata = new Dictionary<string, string> { ["preview"] = "true" },
        });

    public Task<OperationOutcome> ApplyAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default)
    {
        _state = OptimizationState.Applied;
        _headline = Descriptor.StateWhenApplied;
        _explanation = "Preview mode: the card state was updated, but nothing on this PC was changed.";
        _blockedReason = null;
        return Task.FromResult(OperationOutcome.Ok(
            "Preview change applied. No system settings were modified.",
            verified: true));
    }

    public Task<OperationOutcome> RestoreAsync(
        IOptimizationContext context,
        ChangeSet original,
        CancellationToken cancellationToken = default)
    {
        _state = OptimizationState.NotApplied;
        _headline = "Original state restored";
        _explanation = "Preview mode: the card state was updated, but nothing on this PC was changed.";
        _blockedReason = null;
        return Task.FromResult(OperationOutcome.Ok(
            "Preview change undone. No system settings were modified.",
            verified: true));
    }
}

internal static class PreviewOptimizations
{
    public static IReadOnlyList<IOptimization> Create() =>
    [
        new PreviewOptimization(
            Descriptor(
                "mouse-acceleration",
                "Disable Enhance Pointer Precision",
                OptimizationCategory.Mouse,
                RiskLevel.Safe,
                "Remove Windows pointer acceleration so mouse movement stays linear.",
                "HKCU\\Control Panel\\Mouse: MouseSpeed = 0, MouseThreshold1 = 0, MouseThreshold2 = 0. The three REG_SZ values that back the Enhance Pointer Precision checkbox.",
                "Pointer acceleration adds a software curve on top of raw mouse input. Turning it off makes flicks and micro-adjustments more predictable. It does not raise FPS, and some players prefer the default curve.",
                "Windows 10 and 11. Applies to the current user; sign out if the change does not take effect immediately.",
                "Acceleration off",
                ["Original values are captured for all three registry strings.", "Undo restores the exact previous values, including the Windows default state."],
                [new RegistryScope(RegistryHive.CurrentUser, @"Control Panel\Mouse")]),
            OptimizationState.Applied,
            "Enhance pointer precision is off",
            "Windows pointer acceleration is currently disabled.",
            [
                new AssessmentEvidence("MouseSpeed", "0"),
                new AssessmentEvidence("MouseThreshold1", "0"),
                new AssessmentEvidence("MouseThreshold2", "0"),
            ]),
        new PreviewOptimization(
            Descriptor(
                "windows-game-mode",
                "Enable Windows Game Mode",
                OptimizationCategory.Windows,
                RiskLevel.Safe,
                "Keep Windows' Game Mode enabled for game-priority scheduling.",
                "HKCU\\Software\\Microsoft\\GameBar: AutoGameModeEnabled = 1 (DWORD).",
                "Game Mode helps Windows prioritize the foreground game and suppress driver/update interruptions. Effects vary by system; no specific FPS gain is promised.",
                "Windows 10 and 11. The Windows default is usually on.",
                "Game Mode on",
                ["If the value is absent, Windows uses its own default and no override is stored."],
                [new RegistryScope(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar")]),
            OptimizationState.Applied,
            "Game Mode is on (Windows default)",
            "No explicit registry override is stored, so Windows applies its default: enabled.",
            [
                new AssessmentEvidence("AutoGameModeEnabled", "Not set — Windows default on"),
            ]),
        new PreviewOptimization(
            Descriptor(
                "game-bar-capture",
                "Turn off background game capture",
                OptimizationCategory.Windows,
                RiskLevel.Safe,
                "Stop Xbox Game Bar from recording gameplay clips in the background.",
                "HKCU\\System\\GameConfigStore: GameDVR_Enabled = 0 and HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR: AppCaptureEnabled = 0.",
                "Background capture can add encoding overhead while a competitive game is running. Disabling it frees those resources; Game Bar itself can still be opened manually.",
                "Windows 10 and 11. Per-user setting.",
                "Capture off",
                ["Instant replay and clip capture stop working until you undo the change."],
                [
                    new RegistryScope(RegistryHive.CurrentUser, @"System\GameConfigStore"),
                    new RegistryScope(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR"),
                ]),
            OptimizationState.NotApplied,
            "Background capture is enabled",
            "Windows may record clips through Game Bar while you play.",
            [
                new AssessmentEvidence("GameDVR_Enabled", "1"),
                new AssessmentEvidence("AppCaptureEnabled", "1"),
            ]),
        new PreviewOptimization(
            Descriptor(
                "delivery-optimization-peer",
                "Disable Delivery Optimization peering",
                OptimizationCategory.Windows,
                RiskLevel.Advanced,
                "Stop Windows from downloading updates from other PCs on your network or the internet.",
                "HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\DeliveryOptimization\\Config: DODownloadMode = 0 (DWORD). Admin approval is required.",
                "Peer downloads can share your bandwidth and disk while Windows Update runs in the background. Setting HTTP-only mode removes that sharing. It does not change in-game networking.",
                "Windows 10 and 11 Pro/Home with Delivery Optimization enabled.",
                "Peers disabled",
                ["This is a system-wide policy-level change and needs administrator approval.", "Managed by Group Policy when the policy key is present — this toolkit will not override it."],
                [new RegistryScope(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config")]),
            OptimizationState.ReviewRequired,
            "Delivery Optimization is managed by policy",
            "A Group Policy value controls peer downloads on this PC.",
            [
                new AssessmentEvidence("Policy DODownloadMode", "1 — managed"),
                new AssessmentEvidence("Config DODownloadMode", "Not set"),
            ],
            "A policy is present at HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization. Change it in Group Policy instead."),
        new PreviewOptimization(
            Descriptor(
                "valorant-gpu-preference",
                "Set VALORANT to High performance",
                OptimizationCategory.Valorant,
                RiskLevel.Safe,
                "Tell Windows to run the VALORANT client on the high-performance GPU.",
                "HKCU\\Software\\Microsoft\\DirectX\\UserGpuPreferences: per-executable value appended with GpuPreference=2; for VALORANT.exe and VALORANT-Win64-Shipping.exe.",
                "On laptops and multi-GPU desktops Windows can choose a low-power adapter. A per-game high-performance preference avoids that guesswork without changing global graphics settings.",
                "Requires the VALORANT client to be installed. Per-application Windows graphics preference (Windows 10 1803+).",
                "High performance",
                ["Only the VALORANT executables are touched; unrelated UserGpuPreferences entries are preserved."],
                [new RegistryScope(RegistryHive.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences")]),
            OptimizationState.NotApplied,
            "Windows has no per-game preference stored",
            "The game will use the system default GPU selection.",
            [
                new AssessmentEvidence("VALORANT.exe", "GpuPreference not set"),
                new AssessmentEvidence("VALORANT-Win64-Shipping.exe", "GpuPreference not set"),
            ]),
        new PreviewOptimization(
            Descriptor(
                "power-plan-high-performance",
                "Use the High performance power plan",
                OptimizationCategory.CpuPower,
                RiskLevel.Advanced,
                "Keep the CPU from parking or downclocking aggressively during play.",
                "Sets the active Windows power scheme to High performance (8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c) via powercfg. Requires administrator approval; the original scheme is captured for undo.",
                "High performance reduces power-saving transitions at the cost of idle power draw and heat. Laptops may run louder and battery life will drop. It is not a guaranteed FPS improvement.",
                "Windows 10 and 11. The High performance scheme must exist on this PC — Windows 11 may hide it.",
                "High performance",
                ["System-wide change; undo restores the exact original scheme GUID.", "No reboot required."],
                []),
            OptimizationState.NotApplied,
            "Balanced is the active power plan",
            "Windows is using its balanced power scheme.",
            [
                new AssessmentEvidence("Active scheme", "Balanced (381b4222-f694-41f0-9685-ff5bb260df2e)"),
                new AssessmentEvidence("High performance", "Available"),
            ]),
    ];

    private static OptimizationDescriptor Descriptor(
        string id,
        string name,
        OptimizationCategory category,
        RiskLevel risk,
        string summary,
        string whatItChanges,
        string why,
        string compatibility,
        string stateWhenApplied,
        IReadOnlyList<string> notes,
        List<RegistryScope> scopes) => new()
        {
            Id = id,
            Name = name,
            Category = category,
            Risk = risk,
            Summary = summary,
            WhatItChanges = whatItChanges,
            Why = why,
            Compatibility = compatibility,
            StateWhenApplied = stateWhenApplied,
            RequiresElevation = false,
            ManagedScopes = scopes,
            Notes = [.. notes],
        };
}

internal sealed class PreviewBackupStore : IBackupStore
{
    private readonly Dictionary<string, ChangeSet> _backups = new(StringComparer.Ordinal);

    public PreviewBackupStore()
    {
        _backups["mouse-acceleration"] = new ChangeSet
        {
            OptimizationId = "mouse-acceleration",
            CapturedAt = DateTimeOffset.Now.AddMinutes(-18),
            Metadata = new Dictionary<string, string> { ["preview"] = "true" },
        };
    }

    public string DirectoryPath => "(preview: in memory)";

    public Task SaveAsync(ChangeSet changeSet, CancellationToken cancellationToken = default)
    {
        _backups[changeSet.OptimizationId] = changeSet;
        return Task.CompletedTask;
    }

    public Task<ChangeSet?> TryLoadAsync(string optimizationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_backups.TryGetValue(optimizationId, out ChangeSet? changeSet) ? changeSet : null);

    public Task<bool> HasBackupAsync(string optimizationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_backups.ContainsKey(optimizationId));

    public Task DeleteAsync(string optimizationId, CancellationToken cancellationToken = default)
    {
        _backups.Remove(optimizationId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChangeSet>> LoadAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ChangeSet>>(_backups.Values.ToList());
}

internal sealed class PreviewActionLog : IActionLog
{
    private readonly List<ActionLogEntry> _entries =
    [
        new()
        {
            Timestamp = DateTimeOffset.Now.AddHours(-3),
            OptimizationId = "mouse-acceleration",
            OptimizationName = "Disable Enhance Pointer Precision",
            Action = "apply",
            Result = "verified",
            Message = "Pointer acceleration disabled.",
            Changes = [@"HKCU\Control Panel\Mouse\MouseSpeed = 0"],
        },
        new()
        {
            Timestamp = DateTimeOffset.Now.AddDays(-1).AddHours(-2),
            OptimizationId = "valorant-gpu-preference",
            OptimizationName = "Set VALORANT to High performance",
            Action = "apply",
            Result = "verified",
            Message = "VALORANT set to high-performance GPU.",
            Changes = [@"HKCU\Software\Microsoft\DirectX\UserGpuPreferences\C:\Riot Games\VALORANT\live\VALORANT.exe = GpuPreference=2;"],
        },
        new()
        {
            Timestamp = DateTimeOffset.Now.AddDays(-2),
            OptimizationId = "power-plan-high-performance",
            OptimizationName = "Use the High performance power plan",
            Action = "restore",
            Result = "verified",
            Message = "Original power scheme restored.",
            Changes = ["Active scheme -> 381b4222-f694-41f0-9685-ff5bb260df2e"],
        },
    ];

    public Task AppendAsync(ActionLogEntry entry, CancellationToken cancellationToken = default)
    {
        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ActionLogEntry>> ReadRecentAsync(int maxEntries, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ActionLogEntry> result = _entries
            .OrderByDescending(entry => entry.Timestamp)
            .Take(maxEntries)
            .ToList();
        return Task.FromResult(result);
    }
}

internal sealed class PreviewScanService : IScanService
{
    public Task<ScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = new SystemSnapshot
        {
            OsName = "Windows 11 Pro",
            OsVersion = "10.0.26100",
            OsBuild = "26100.2314",
            OsDisplayVersion = "24H2",
            Architecture = "X64",
            Cpu = new CpuInfo("AMD Ryzen 7 7800X3D 8-Core Processor", 8, 16, 4200),
            Gpus =
            [
                new GpuInfo("NVIDIA GeForce RTX 4070 SUPER", "NVIDIA", "566.36", "2024-12-05", 12UL * 1024 * 1024 * 1024),
            ],
            TotalMemoryBytes = 32UL * 1024 * 1024 * 1024,
            PrimaryDisplay = new DisplayInfo(@"\\.\DISPLAY1", 2560, 1440, 240, 32),
            IsElevated = false,
            Warnings = [],
        };

        var valorant = new ValorantInstallation
        {
            ExecutablePath = @"C:\Riot Games\VALORANT\live\VALORANT.exe",
            ShippingExecutablePath = @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe",
            InstallDirectory = @"C:\Riot Games\VALORANT",
            Version = "10.10.02",
            DetectionSource = "Preview data",
        };

        return Task.FromResult(new ScanResult(snapshot, valorant));
    }
}

internal sealed class PreviewStartupAppScanner : IStartupAppScanner
{
    public Task<IReadOnlyList<StartupAppEntry>> ScanAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StartupAppEntry> entries =
        [
            new()
            {
                Name = "Riot Vanguard",
                Command = @"C:\Program Files\Riot Vanguard\vgtray.exe",
                Source = StartupSource.MachineRunKey,
                IsEnabled = true,
                Publisher = "Riot Games, Inc.",
                Flags = ["Anti-cheat", "Do not disable"],
                Note = "Required by VALORANT. Never disable or terminate anti-cheat components.",
            },
            new()
            {
                Name = "Riot Client",
                Command = @"C:\Riot Games\Riot Client\RiotClientServices.exe --launch-background-mode",
                Source = StartupSource.UserRunKey,
                IsEnabled = true,
                Publisher = "Riot Games, Inc.",
                Flags = ["Game platform"],
            },
            new()
            {
                Name = "NVIDIA App",
                Command = @"C:\Program Files\NVIDIA Corporation\NVIDIA app\CEF\NVIDIA App.exe --autostart",
                Source = StartupSource.MachineRunKey,
                IsEnabled = true,
                Publisher = "NVIDIA Corporation",
                Flags = ["GPU driver"],
            },
            new()
            {
                Name = "Discord",
                Command = @"C:\Users\User\AppData\Local\Discord\Update.exe --processStart Discord.exe",
                Source = StartupSource.UserRunKey,
                IsEnabled = true,
                Publisher = "Discord Inc.",
                Flags = ["Background app"],
            },
            new()
            {
                Name = "Steam",
                Command = @"""C:\Program Files (x86)\Steam\steam.exe"" -silent",
                Source = StartupSource.UserRunKey,
                IsEnabled = true,
                Publisher = "Valve Corporation",
                Flags = ["Game platform"],
            },
            new()
            {
                Name = "Realtek Audio Console",
                Command = @"C:\Program Files\Realtek\Audio\HDA\RtkAudioService.exe",
                Source = StartupSource.MachineRunKey,
                IsEnabled = false,
                Publisher = "Realtek Semiconductor",
                Flags = ["Audio driver"],
                Note = "Disabling audio services can remove sound devices. Keep disabled only if you know it is unused.",
            },
            new()
            {
                Name = "Microsoft OneDrive",
                Command = @"C:\Users\User\AppData\Local\Microsoft\OneDrive\OneDrive.exe /background",
                Source = StartupSource.UserRunKey,
                IsEnabled = true,
                Publisher = "Microsoft Corporation",
                Flags = ["Background app"],
            },
            new()
            {
                Name = "Spotify",
                Command = @"C:\Users\User\AppData\Roaming\Spotify\Spotify.exe --autostart --minimized",
                Source = StartupSource.UserStartupFolder,
                IsEnabled = true,
                Publisher = "Spotify AB",
                Flags = ["Background app"],
            },
        ];

        return Task.FromResult(entries);
    }
}
