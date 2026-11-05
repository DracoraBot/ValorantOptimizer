# Verification log

Last run: 2026-11-04

## Automated tests

```
dotnet build ValorantOptimizer.sln -c Release
dotnet test  ValorantOptimizer.sln -c Release --no-build
```

Result: **Build succeeded, 0 warnings, 0 errors. 437 tests passed.**

| Suite | Tests | Coverage |
|---|---:|---|
| `ValorantOptimizer.Core.Tests` | 59 | backup store, schema rejection, JSONL log, registry codec, snapshot/restore, engine apply/undo/rollback, scope validation, elevation protocol, catalog validation |
| `ValorantOptimizer.Diagnostics.Tests` | 103 | OS/CPU/GPU/RAM/display parsing, software-adapter filtering, VALORANT locator precedence + scan cache, startup approval states, scan composition, live CPU/RAM/NVIDIA metrics |
| `ValorantOptimizer.Optimizations.Tests` | 275 | all 21 module descriptors, assessment states, capture content, apply writes, undo for present/absent values, mouse live-session refresh, policy-managed Delivery Optimization, GPU preference merge, HAGS build gate, appcompat token merge, powercfg parsing/restore, core parking, USB selective suspend, network adapter targeting, review-only refusal and zero-write behavior, catalog integrity |

All tests use `FakeRegistryStore`, `FakeProcessRunner`, `TempDirectory` and fake locators — no test
writes to the real registry, changes power plans, or runs `powercfg`/`nvidia-smi`.

## Live read-only smoke test

```
dist\ValorantOptimizer.exe --smoke "C:\path\scan-smoke.json"
```

Result: exit code 0, `hasScanError: false`.

The scan found Windows edition/build, CPU, GPU + driver, RAM, display mode, elevation state and
the installed VALORANT client. All 21 cards returned an assessment (no Unknown states):

- 4 `Applied`
- 8 `NotApplied`
- 7 `ReviewRequired` (read-only review cards)
- 2 `Unsupported` (High performance power plan and core-parking setting hidden on this Windows 11
  machine — both modules correctly refused instead of guessing)

The full assessment completes in about 4 seconds on this machine (locator results are shared
between the scan and the VALORANT modules; the temp-size review has a bounded 1.2 s budget).
No system change was made: the smoke path only reads state and writes the JSON report.

The same smoke path was run from the published self-contained `dist` build, confirming that the
single-file packaging starts, loads the real composition root and completes the scan.

## UI verification

The WPF app was driven end-to-end through Windows UI Automation against the built-in preview
dataset (`--demo`, no real writes), and the live build was launched read-only:

| Flow | Result |
|---|---|
| Live first launch + scan | Pass — real summary tiles and 21 assessments, no crash log |
| Unified sidebar + 3-column grid | Pass — one Optimizations entry, no filter chips, review/unsupported cards greyed out |
| Card toggles (apply / undo) | Pass — toggle apply and restore flows confirmed; review/unsupported/no-backup toggles disabled with explanations |
| Card details drawer | Pass — evidence, caveats, original-state note, managed locations |
| Apply confirmation + acknowledgements | Pass — safe change applied, verified, toast shown |
| Bulk "Apply safe recommendations" | Pass — 2 of 2 applied with one summary toast |
| Undo with captured backup | Pass — originals restored, verified, toast shown |
| Startup apps review | Pass — source, publisher, flags, enabled state, anti-cheat warning |
| Action history | Pass — applied/restored rows with verified results and changed values |
| Live monitoring | Pass — CPU/GPU/RAM/GPU temperature and top processes sampled; FPS/frame time/CPU temp explicitly shown unavailable |
| Window chrome / DPI / text rendering | Pass — custom title bar, 1320×860 default, PerMonitorV2 manifest |

Screenshots: `docs/images/overview.png`, `detail.png`, `confirm.png`, `startup.png`,
`history.png`, `monitoring.png`. They use the preview dataset so no personal hardware details are
published.

## Launch animation

The shell is covered by a boot overlay while the first scan runs. Only `Opacity` and render
transforms are animated, so the scan never competes with a layout pass.

| Case | Result |
|---|---|
| Live launch (real scan, about 4 s) | Pass — overlay visible through the scan, fades out when it finishes, no crash log entry |
| `--demo --splash`, intro frozen at 0.34 s | Pass — mark, wordmark, progress rail and sweep all render as composed |
| `--demo` without `--splash` | Pass — overlay collapsed before first paint, so UI Automation still finds `MainWindow` |
| `VALORANT_OPTIMIZER_NO_ANIM=1` | Pass — static end frame plus a static progress segment, no motion |
| `--smoke` | Pass — exit code 0, no window, unchanged |

Reduced motion uses the same path through `SystemParameters.ClientAreaAnimation`.

Frames: `artifacts/screenshots/boot-t034.png` (held intro), `boot-live-early.png` (live launch),
`boot-after.png` (after the fade), `boot-reduced.png` (motion disabled).

## Published artifacts

`tools\build.ps1 -CreateZip` produced:

```
dist\ValorantOptimizer.exe              (self-contained, single file)
dist\helper\ValorantOptimizer.Elevated.exe
dist\SHA256SUMS.txt
artifacts\ValorantOptimizer-0.1.0-win-x64.zip
```

Verification performed:

- published app smoke run exit code 0, valid JSON report, **21 cards assessed with no Unknown
  states**, about 4.2 seconds end-to-end;
- published `--demo` UI loaded all 21 toggles from the catalog;
- helper executable present in `dist\helper` (the elevation client resolves this path);
- every file listed in `SHA256SUMS.txt` hashes cleanly.

## Not covered by automation (manual verification required)

- **FPS, frame times, CPU temperature and non-NVIDIA GPU telemetry.** These need a frame-capture
  hook, a hardware sensor service, or vendor APIs that are not bundled. The Monitoring page shows
  them as unavailable and the README documents the limitation.
- **UAC approval of an actual privileged write.** The elevation helper is compiled with
  `requireAdministrator`, but Windows cannot show and approve a UAC prompt from an automated test.
  Use the checklist in [SAFETY.md](SAFETY.md#manual-privileged-change-test-checklist) on a test
  machine.
- **Cross-hardware variation** (hybrid-GPU laptops, multiple displays, unusual network adapters,
  different Windows builds). The cards are designed to degrade to `Unsupported`/`Needs review`
  instead of guessing.
- **Code signing.** Release binaries are unsigned; checksums are provided for verification.

## Reproduce

```powershell
cd ValorantOptimizer
dotnet test ValorantOptimizer.sln -c Release
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -CreateZip
dist\ValorantOptimizer.exe --smoke "$env:TEMP\scan-smoke.json"
```
