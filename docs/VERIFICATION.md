# Verification log

Last run: 2026-11-04

## Automated tests

```
dotnet build ValorantOptimizer.sln -c Release
dotnet test  ValorantOptimizer.sln -c Release --no-build
```

Result: **Build succeeded, 0 warnings, 0 errors. 225 tests passed.**

| Suite | Tests | Coverage |
|---|---:|---|
| `ValorantOptimizer.Core.Tests` | 59 | backup store, schema rejection, JSONL log, registry codec, snapshot/restore, engine apply/undo/rollback, scope validation, elevation protocol, catalog validation |
| `ValorantOptimizer.Diagnostics.Tests` | 67 | OS/CPU/GPU/RAM/display parsing, software-adapter filtering, VALORANT locator precedence, startup approval states, scan composition |
| `ValorantOptimizer.Optimizations.Tests` | 99 | all six module descriptors, assessment states, capture content, apply writes, undo for present/absent values, mouse live-session refresh, policy-managed Delivery Optimization, GPU preference merge, powercfg parsing, catalog integrity |

All tests use `FakeRegistryStore`, `FakeProcessRunner`, `TempDirectory` and fake locators — no test
writes to the real registry, changes power plans, or runs `powercfg`.

## Live read-only smoke test

```
dist\ValorantOptimizer.exe --smoke "C:\path\scan-smoke.json"
```

Result: exit code 0, `hasScanError: false`.

The scan found Windows edition/build, CPU, GPU + driver, RAM, display mode, elevation state and
the installed VALORANT client. All six cards returned an assessment:

- 1 `Applied`
- 4 `NotApplied`
- 1 `Unsupported` (the High performance power plan is hidden on this Windows 11 Home machine —
  the module correctly refused instead of creating a scheme)

No system change was made: the smoke path only reads state and writes the JSON report.

The same smoke path was run from the published self-contained `dist` build, confirming that the
single-file packaging starts, loads the real composition root and completes the scan.

## UI verification

The WPF app was driven end-to-end through Windows UI Automation against the built-in preview
dataset (`--demo`, no real writes), and the live build was launched read-only:

| Flow | Result |
|---|---|
| Live first launch + scan | Pass — real summary tiles and six assessments, no crash log |
| Category navigation and state filters | Pass |
| Card details drawer | Pass — evidence, caveats, original-state note, managed locations |
| Apply confirmation + acknowledgements | Pass — safe change applied, verified, toast shown |
| Bulk "Apply safe recommendations" | Pass — 2 of 2 applied with one summary toast |
| Undo with captured backup | Pass — originals restored, verified, toast shown |
| Startup apps review | Pass — source, publisher, flags, enabled state, anti-cheat warning |
| Action history | Pass — applied/restored rows with verified results and changed values |
| Window chrome / DPI / text rendering | Pass — custom title bar, 1320×860 default, PerMonitorV2 manifest |

Screenshots: `docs/images/overview.png`, `detail.png`, `confirm.png`, `startup.png`,
`history.png`. They use the preview dataset so no personal hardware details are published.

## Published artifacts

`tools\build.ps1 -CreateZip` produced:

```
dist\ValorantOptimizer.exe              (self-contained, single file)
dist\helper\ValorantOptimizer.Elevated.exe
dist\SHA256SUMS.txt
artifacts\ValorantOptimizer-0.1.0-win-x64.zip
```

Verification performed:

- published app smoke run exit code 0 and valid JSON report;
- helper executable present in `dist\helper` (the elevation client resolves this path);
- every file listed in `SHA256SUMS.txt` hashes cleanly.

## Not covered by automation (manual verification required)

- **UAC approval of an actual privileged write.** The elevation helper is compiled with
  `requireAdministrator`, but Windows cannot show and approve a UAC prompt from an automated test.
  Use the checklist in [SAFETY.md](SAFETY.md#manual-privileged-change-test-checklist) on a test
  machine.
- **Cross-hardware variation** (hybrid-GPU laptops, multiple displays, different Windows builds).
  The cards are designed to degrade to `Unsupported`/`Needs review` instead of guessing.
- **Code signing.** Release binaries are unsigned; checksums are provided for verification.

## Reproduce

```powershell
cd ValorantOptimizer
dotnet test ValorantOptimizer.sln -c Release
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -CreateZip
dist\ValorantOptimizer.exe --smoke "$env:TEMP\scan-smoke.json"
```
