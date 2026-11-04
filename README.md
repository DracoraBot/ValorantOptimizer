# VALORANT PC Optimization Toolkit

A Windows desktop app that scans your PC, explains specific gaming-related Windows settings, and lets you apply, verify and undo each change. Built with C#/.NET 10 and WPF.

It is deliberately **not** a script collection or a one-click "FPS booster". Every card is a managed change with a captured backup, a verification step, and a one-click rollback.

> Unofficial tool. Not affiliated with, endorsed by, or sponsored by Riot Games. VALORANT and Riot Games are trademarks of Riot Games, Inc.

![Overview](docs/images/overview.png)

*Screenshots use the built-in preview dataset (`--demo`) so no personal hardware details are shown.*

## What it does

### System scan
Reads Windows edition/build, CPU, GPU and driver version, installed RAM, primary display mode/refresh rate, elevation state, and the installed VALORANT client path — without administrator rights. The summary strip shows what the toolkit found; every card shows its own evidence.

### Modular optimization grid
Cards are grouped by Mouse, Windows, CPU & power, and GPU & VALORANT in a uniform three-column grid. Each card shows a category icon, risk label, current state, short explanation and a toggle switch. Turning a toggle on opens the apply confirmation; turning a managed toggle off restores the captured backup. Toggles are disabled (with an explanatory tooltip) when Windows owns the state, a policy is in place, the optimization is unsupported, or no backup exists. The details drawer adds:

- exactly what is written (registry paths and values),
- why it might help (mechanism, no promised FPS numbers),
- compatibility notes and caveats,
- the captured original values,
- the registry scopes the module is allowed to touch.

### MVP modules

| ID | Card | Recommended state | Risk | Admin |
|---|---|---|---|---|
| `mouse-acceleration` | Disable mouse acceleration | `MouseSpeed`/`MouseThreshold1`/`MouseThreshold2` = `"0"` (REG_SZ), then `SPI_SETMOUSE` refresh | Safe | No |
| `windows-game-mode` | Windows Game Mode | `AutoGameModeEnabled` = 1 (DWORD) | Safe | No |
| `game-bar-capture` | Xbox Game Bar background capture | `GameDVR_Enabled` = 0 and `AppCaptureEnabled` = 0 | Safe | No |
| `delivery-optimization-peer` | Delivery Optimization peer downloads | `DODownloadMode` = 0 (HTTP only) | Advanced | Yes |
| `valorant-gpu-preference` | VALORANT high-performance GPU | per-exe `GpuPreference=2;` for the launcher and shipping EXE | Safe | No |
| `power-plan-high-performance` | High performance power plan | active scheme → `8c5e7fda-…` via `powercfg` | Advanced | Yes |

Game Mode's missing registry value is treated as Windows' default *on*, not as "disabled". Delivery Optimization is reported as policy-managed and left alone when a Group Policy value exists. The High performance power plan is reported **Unsupported** when Windows 11 hides it; the toolkit never creates or modifies power schemes.

### Apply safe recommendations
When safe, non-elevated recommendations are available, one button applies them in sequence — each still captured, verified and individually undoable.

### Startup apps review
A read-only table of Run keys, Startup-approved entries and Startup-folder items, with publisher, command line, source, enabled state and risk flags (anti-cheat, GPU, audio, background apps). Nothing is disabled here; Windows Task Manager remains the owner of that state so there is always a native undo path.

### Local action history
Every apply, verification and undo is written to a human-readable JSONL file. The History page shows the newest results, changed values, timestamps, and links to the log folder. Nothing is uploaded or sent anywhere.

## Safety model

- **Backup before write.** The engine captures the exact original values (including "value did not exist") and persists a schema-versioned JSON backup before applying anything.
- **Verify after write.** Apply is followed by a fresh assessment; the result is only `Verified` when the expected state is observed. A verified apply is logged as `verified`, otherwise `applied-unverified` with a warning.
- **Automatic rollback.** If an in-process module throws or reports failure, the engine restores the captured values and verifies the restore. The elevated helper performs the same rollback inside one UAC session.
- **Scope validation.** Undo data is rejected unless every entry belongs to the module's declared `ManagedScopes`. The helper re-validates the request before writing.
- **On-demand elevation only.** The UI runs as the signed-in user. Privileged changes launch the separate `ValorantOptimizer.Elevated.exe` helper with a single request, and the helper exits afterwards. The main window never runs permanently as administrator.
- **No security or anti-cheat changes.** The toolkit never disables Defender, the firewall, Windows Update, Secure Boot, Riot Vanguard or other anti-cheat components, and never terminates anti-cheat processes.
- **No magic numbers.** No kernel drivers, undocumented timer hacks, blind service debloating, or generic "latency fixes". No FPS number is promised without controlled measurement on that machine.
- **Local-only.** No telemetry, network calls or cloud account. Logs and backups stay in `%LOCALAPPDATA%\ValorantOptimizer`.

See [docs/SAFETY.md](docs/SAFETY.md) for the guarantees and their implementation.

## Screenshots

| | |
|---|---|
| ![Details drawer](docs/images/detail.png) | ![Confirmation](docs/images/confirm.png) |
| ![Startup review](docs/images/startup.png) | ![History](docs/images/history.png) |

## Requirements

- Windows 10 or 11, x64.
- To build: [.NET 10 SDK](https://dotnet.microsoft.com/download) (the repo targets `net10.0-windows`).
- To run a framework-dependent build: .NET 10 Desktop Runtime.
- The self-contained release build needs no installed runtime.

## Build, run and test

```powershell
cd ValorantOptimizer

dotnet build ValorantOptimizer.sln
dotnet test ValorantOptimizer.sln

# Normal live run (read-only until you press Apply)
dotnet run --project src\ValorantOptimizer.App

# UI preview with synthetic data and no system access
dotnet run --project src\ValorantOptimizer.App -- --demo

# Headless read-only scan + assessment, writes a JSON summary and exits
dotnet run --project src\ValorantOptimizer.App -- --smoke artifacts\scan-smoke.json
```

The first launch only reads state. No change happens until you explicitly confirm an Apply action.

## Publish

```powershell
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -CreateZip
```

This builds, tests, publishes the app and the elevated helper as self-contained single-file executables, copies documentation, writes `SHA256SUMS.txt`, and optionally creates a zip in `artifacts/`.

```
dist/
  ValorantOptimizer.exe              app (unelevated)
  helper/
    ValorantOptimizer.Elevated.exe   one-shot elevated helper
  README.md
  docs/
  SHA256SUMS.txt
```

For a smaller, framework-dependent build:

```powershell
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -FrameworkDependent
```

Release binaries are currently **unsigned**. SmartScreen may warn on first launch; verify the SHA-256 checksums before running. Code signing and a public checksum release process are planned.

## Where data lives

| Path | Contents |
|---|---|
| `%LOCALAPPDATA%\ValorantOptimizer\backups\<id>.backup.json` | Per-optimization original state, schema version, timestamp |
| `%LOCALAPPDATA%\ValorantOptimizer\logs\actions.jsonl` | Apply/verify/undo history |
| `%LOCALAPPDATA%\ValorantOptimizer\logs\elevated.log` | Elevated helper activity |
| `%LOCALAPPDATA%\ValorantOptimizer\logs\ui-crash.log` | UI crash details |
| `%LOCALAPPDATA%\ValorantOptimizer\elevation\` | Temporary request/response files, deleted after use |

The sidebar's **Open data folder** button opens the root. Backups and logs are plain JSON/JSONL and can be inspected or deleted while the app is closed. Deleting a backup removes the ability to Undo that card.

## Architecture

| Project | Responsibility |
|---|---|
| `ValorantOptimizer.Core` | Contracts, registry abstraction, schema-versioned backup store, JSONL action log, optimization engine, elevation protocol |
| `ValorantOptimizer.Diagnostics` | Read-only system snapshot, VALORANT locator, startup-app scanner |
| `ValorantOptimizer.Optimizations` | Six MVP modules and the explicit catalog |
| `ValorantOptimizer.App` | WPF shell, grid UI, view models, elevation client, composition root |
| `ValorantOptimizer.Elevated` | One-shot administrator helper with its own manifest |
| `tests/*` | Core, Diagnostics and Optimizations unit tests using fakes only |

Lifecycle: **assess → capture → apply → verify → (undo) restore → verify**. See [docs/ENGINE-CONTRACT.md](docs/ENGINE-CONTRACT.md) for the full module contract and the rules for adding an optimization.

### Add an optimization

1. Implement `IOptimization` in `src/ValorantOptimizer.Optimizations/<Category>/`.
2. Use `RegistrySnapshot.CaptureMany` / `RestoreAll`; never hand-roll capture or restore.
3. Declare every writable registry location in `ManagedScopes`, set `RequiresElevation` for HKLM/powercfg changes, and describe the change honestly.
4. Add tests with `FakeRegistryStore`/`FakeProcessRunner` covering apply, undo, and originally-absent values.
5. Register it in `OptimizationCatalog.CreateDefault` — one line.

`OptimizationCatalogValidator` runs at engine startup and rejects duplicate/invalid ids.

## Limitations and accuracy notes

- **No NVIDIA Control Panel automation.** G-SYNC, power management and texture filtering are hardware/preference-dependent; they are deliberately excluded from the MVP instead of guessed.
- **Hardware-accelerated GPU scheduling (HAGS)** is not in the MVP: it requires a reboot and specific driver support, so it is not guessed at.
- **In-game VALORANT settings** (raw input buffer, graphics options) are not edited; the toolkit manages Windows-side configuration only.
- **High performance plan may be missing.** Windows 11 Home often hides it. The card reports Unsupported; it does not create a scheme.
- **Mouse acceleration can be bypassed** by games using raw input (VALORANT does). The change is still relevant to Windows pointer behavior; pointer speed (`MouseSensitivity`) is intentionally untouched.
- **Game Mode defaults on.** Missing registry state is reported as the Windows default rather than "disabled".
- **Delivery Optimization** affects Windows Update/Store peer downloads, not in-game networking. Policy-managed PCs are review-only.
- **Startup review is read-only** by design in this MVP. It identifies risky entries instead of disabling them.
- **No restore point creation.** The toolkit relies on exact per-setting backups. It does not perform large system changes that would warrant a restore point.
- **No FPS claims.** Cards explain mechanisms; performance depends on hardware, drivers and the game version.
- Windows builds differ; every card shows the evidence it found so unsupported/unknown states are visible.

## Verification

The repository has **225 unit tests** (Core 59, Diagnostics 67, Optimizations 99), all using fakes — no test modifies the real machine. The WPF UI was exercised end-to-end against the preview dataset (details, confirmation, apply, bulk safe apply, undo, startup review, history) and the live app was smoke-tested read-only. Details: [docs/VERIFICATION.md](docs/VERIFICATION.md).

## Trademark and privacy

VALORANT and Riot Games are trademarks of Riot Games, Inc. This project is independent and not affiliated with or endorsed by Riot Games. The toolkit does not collect or transmit any data; all state stays on the local PC.
