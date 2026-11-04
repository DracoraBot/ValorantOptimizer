# Safety model

The toolkit's promise is **reversible, verifiable configuration changes**. This document maps
each guarantee to the code that enforces it and lists what the application deliberately refuses
to do.

## Guarantees

### 1. Nothing is written before the original state is saved

`OptimizationEngine.ApplyAsync` (Core) performs:

1. a fresh read-only assessment,
2. `IOptimization.CaptureAsync` for the exact original values,
3. `JsonBackupStore.SaveAsync` to
   `%LOCALAPPDATA%\ValorantOptimizer\backups\<id>.backup.json`,
4. only then the apply step.

If capture returns no registry entries **and** no metadata, the engine refuses the change. If the
backup cannot be written, nothing is changed. The file is written to a temporary path and
atomically moved into place.

Backups carry:

- `schemaVersion` (unknown versions are rejected, never guessed),
- optimization id,
- capture timestamp,
- exact value existence, registry kind and canonical data.

### 2. Verification is independent of the module's own claim

After a successful apply the engine runs `AssessAsync` again and marks the operation verified only
when the module reports the expected `Applied` state. Registry-based undo is additionally checked
by `ChangeSetInspector`, which compares every captured value (existence, kind, serialized data) to
the live registry rather than trusting the restore return value.

The History page shows `Verified`, `unverified`, `rolled back` or `failed` accordingly.

### 3. Failed changes are rolled back automatically

- In-process apply exception or `Success = false` → engine calls `RestoreAsync` with the captured
  original and verifies the values.
- Elevated apply failure → the helper itself calls `RestoreAsync` before exiting, so the user is
  not prompted for a second UAC approval. The response reports `rolledBack`.
- If rollback cannot be verified, the backup is preserved and the user is told to retry Undo.

### 4. Undo is scope-restricted

`RestoreScopeValidator` rejects a backup unless:

- it belongs to the same optimization id,
- its schema version is supported,
- every entry lies inside the descriptor's declared `ManagedScopes`,
- existing values have a known registry kind.

This check runs in the UI process and again in the elevated helper (which treats its JSON request
as untrusted input). A corrupted or hand-edited backup therefore cannot cause an arbitrary
registry write.

### 5. Elevation is on demand only

- The WPF app manifest is `asInvoker`; the UI runs as the signed-in user.
- Modules set `RequiresElevation = true` only for HKLM or `powercfg /setactive` changes.
- The engine persists the backup, then launches `ValorantOptimizer.Elevated.exe` with
  `Verb=runas` and a single JSON request.
- The helper has a `requireAdministrator` manifest, runs one known optimization by id, validates
  the request, writes a JSON response and exits. It is not a service and is never left running.
- UAC cancellation is handled as a normal "nothing changed" result.

### 6. Security and anti-cheat are out of bounds

The toolkit never disables, stops, deletes or alters:

- Microsoft Defender, the Windows firewall, Windows Update or Secure Boot,
- Riot Vanguard or any anti-cheat service/process,
- security-related services, scheduled tasks or drivers.

No module may write outside its declared scopes. The Startup review screen is read-only and flags
anti-cheat, GPU, audio and system entries rather than changing them.

### 8. Review-only cards never write

Seven cards are inspection surfaces rather than changes: pointer speed, NVIDIA driver settings,
memory overview, network/DNS overview, USB device power, storage overview and VALORANT
configuration. They return `ReviewRequired` (or `Unsupported` when absent), expose evidence, and
return a failure if Apply/Restore is ever invoked. Their toggles are disabled and the cards are
greyed out. No registry write, file write or process launch occurs during their assessment.

### 9. Monitoring is read-only

The Monitoring page samples CPU time (`GetSystemTimes`), memory (`GlobalMemoryStatusEx`) and, when
available, NVIDIA utilization/temperature via a read-only `nvidia-smi` query. It launches no
elevated process, changes nothing, and marks FPS, frame times and CPU temperature as unavailable
rather than inferring values. See `SystemMetricsProvider` and `MonitoringViewModel`.

### 10. New privileged modules follow the same engine

CPU power throttling, CPU foreground scheduling, HAGS, core parking, USB selective suspend and
network-adapter power are all handled by the same capture → backup → apply → verify → rollback
engine. Each declares its exact registry/powercfg scope, requires explicit UAC approval through the
helper, and reports reboot requirements. Review-only modules never appear in the Apply-safe batch.

### 7. Local and inspectable

No telemetry, accounts, cloud calls or remote logging. The app writes only under the current
user's `%LOCALAPPDATA%\ValorantOptimizer` folder. Logs and backups are plain JSON/JSONL and can be
opened with the sidebar/history buttons.

## Failure semantics

| Situation | Result |
|---|---|
| Assessment throws | Card shows `Unknown`; Apply is refused |
| Capture empty | Apply refused before any write |
| Backup write fails | Apply refused; no system change |
| Apply throws / reports failure (unelevated) | Original values restored; `rolledBack` reported when verified |
| Helper apply fails | Helper rolls back internally; UI reports failure/rolled back |
| Verification mismatch after apply | Success with `applied-unverified`, warning + backup retained |
| Undo without a backup | Clear failure; no writes |
| Corrupt / future-schema backup | Read error surfaced; no writes |
| UAC cancelled | "Nothing was changed" |
| Registry value did not exist originally | Undo deletes it instead of writing a guessed default |

## Deliberate non-goals (MVP)

- No NVIDIA Control Panel automation (G-SYNC, texture filtering, power management) without
  validated driver interfaces.
- No kernel drivers, timer-resolution hacks, MSI-mode tweaks, service debloating, debloat scripts,
  network "latency" recipes or anti-cheat workarounds.
- No global graphics settings changes when a per-game setting is available.
- No startup-app disabling in this version.
- No FPS/frame-time overlay or CPU-temperature sensor integration. Those need a capture/sensor
  service that is not bundled; the Monitoring page says so instead of estimating.
- No automatic restore-point creation. Per-setting backups are the undo mechanism; the app avoids
  changes large enough to need a system restore point.
- No FPS guarantees. Cards explain mechanisms and show exactly what they found.

## Residual risks and user responsibilities

- **Unsigned binaries.** Verify the SHA-256 checksums in `SHA256SUMS.txt`. SmartScreen may warn.
- **Local attacker / same-user process.** Backups and logs live in the user's profile and are
  editable by that user. The elevated helper accepts only known optimization ids and validated
  scopes, and still requires a UAC approval, but it cannot defend against a fully compromised
  interactive account.
- **Windows build differences.** Registry locations and policy behavior vary by edition/build.
  Evidence is shown on every card; `Unsupported`/`Needs review` means the tool declined to guess.
- **Removing backups.** Deleting `%LOCALAPPDATA%\ValorantOptimizer\backups` removes the ability to
  Undo existing changes. Capture a fresh backup by re-applying the card only if the state is
  currently the original one; otherwise restore manually from the values shown in the details
  drawer.

## Manual privileged-change test checklist

Because UAC cannot be automated safely, verify on a test machine:

1. Run the app normally (standard user) and open the Delivery Optimization card.
2. Click Apply → confirm → UAC prompt appears for `ValorantOptimizer.Elevated.exe`.
3. Approve: check the card becomes Applied/verified and the toggle is written under
   `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config`.
4. Undo: confirm the original value returns and the backup file exists.
5. Repeat and cancel UAC: nothing is written and no failure dialog blocks the UI.
6. Check `logs\actions.jsonl` and `logs\elevated.log` for both attempts.
7. Repeat for one power-plan-setting card (core parking or USB selective suspend): the original
   AC value should be restored via `powercfg` and the log should show the exact commands.
8. Check a review-only card (e.g. NVIDIA settings): its toggle must stay disabled, nothing should
   be written, and the details should show the detected evidence.
