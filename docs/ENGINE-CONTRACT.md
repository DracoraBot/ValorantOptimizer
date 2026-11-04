# Optimization engine contract

This document is the API contract shared by the engine, optimization modules, tests, the WPF
shell, and the elevated helper. Read it before adding a module.

## Dependency graph

```
Core            registry / backup / logging / engine / contracts   (Windows TFM, no WPF)
Diagnostics     system scan, VALORANT locator, startup review      (references Core)
Optimizations   concrete optimization modules                      (references Core + Diagnostics)
App (WPF)       UI, composition, elevation client                  (references all)
Elevated        per-operation administrator helper                 (references Core + Optimizations)
TestSupport     fake registry / process / optimization             (tests only)
```

The Core project must never reference Diagnostics, Optimizations, or WPF.
A module may not reference another module.

## Lifecycle

Every optimization implements `IOptimization` and is managed by `OptimizationEngine`:

1. `AssessAsync` — read-only inspection. Must never write, launch installers, or change state.
2. `CaptureAsync` — returns the exact original state as a `ChangeSet`.
3. `ApplyAsync` — writes the recommended configuration. Receives the captured `ChangeSet`.
4. Engine re-runs `AssessAsync` and only reports `Verified = true` when the state is `Applied`.
5. Undo loads the persisted `ChangeSet`, validates scopes, calls `RestoreAsync`, then the engine
   compares every captured registry value to the live machine with `ChangeSetInspector`.

If `ApplyAsync` returns `Success = false` or throws, the engine automatically calls
`RestoreAsync` with the captured original (in-process modules). The elevated helper performs the
same rollback for privileged modules. Backups are persisted *before* the write starts.

## Required interfaces

```csharp
public interface IOptimization
{
    OptimizationDescriptor Descriptor { get; }
    Task<OptimizationAssessment> AssessAsync(IOptimizationContext ctx, CancellationToken ct = default);
    Task<ChangeSet> CaptureAsync(IOptimizationContext ctx, CancellationToken ct = default);
    Task<OperationOutcome> ApplyAsync(IOptimizationContext ctx, ChangeSet original, CancellationToken ct = default);
    Task<OperationOutcome> RestoreAsync(IOptimizationContext ctx, ChangeSet original, CancellationToken ct = default);
}
```

`IOptimizationContext` exposes `Registry` (`IRegistryStore`), `Processes` (`IProcessRunner`) and
`IsElevated`. Use `RegistrySnapshot.Capture` / `CaptureMany` / `Restore` so the serialized
original values round-trip exactly; never capture values by hand.

## Descriptor rules

- `Id`: lowercase ASCII letters/digits/`.`/`-`, 3–80 chars, stable forever. Example:
  `mouse-acceleration`, `delivery-optimization-peer`.
- `Category`: `Mouse`, `Windows`, `CpuPower`, `Gpu`, `Valorant`, `Startup` (UI groups by this).
- `Risk`: `Safe` (broadly safe, reversible), `Advanced` (system/policy-wide, confirm),
  `Experimental` (hardware/driver dependent; must explain risk).
- `RequiresElevation = true` if writes touch `HKLM` or run `powercfg /setactive`. The engine then
  routes apply/undo through the elevated helper; do not attempt to self-elevate.
- `ManagedScopes` must list every registry location the module can write or restore. Undo data
  outside these scopes is rejected by `RestoreScopeValidator` (also enforced in the helper).
- `Restart`: `None`, `SignOut`, or `Reboot` — only when the change genuinely needs it.
- Never promise FPS numbers. Explain the mechanism and mark hardware dependence.

## Writing settings

```csharp
// capture
var original = new ChangeSet
{
    OptimizationId = Descriptor.Id,
    Entries = RegistrySnapshot.CaptureMany(ctx.Registry,
        new RegistryTarget(RegistryHive.CurrentUser, Key, "MouseSpeed"),
        new RegistryTarget(RegistryHive.CurrentUser, Key, "MouseThreshold1")),
};

// apply
ctx.Registry.WriteValue(RegistryHive.CurrentUser, Key, "MouseSpeed",
    RegistryValueKind.String, "0");

// restore
RegistrySnapshot.RestoreAll(ctx.Registry, original.Entries);
```

Rules:

- Only write values the module owns. Never touch policies, security, Defender, firewall, Windows
  Update, Secure Boot, Riot Vanguard, or anti-cheat processes.
- Preserve unknown content: when rewriting a compound string (for example
  `UserGpuPreferences`), keep all unrelated `Key=Value;` pairs.
- Deleting is a real state: a value that did not exist must be deleted on undo, not written as a
  default you guessed.
- Read values with the exact registry kind you expect; `DWord` vs `String` mistakes are bugs.
- Do not trust localized tool output. Parse stable identifiers (GUIDs, exit codes) and fall back
  to `ReviewRequired` instead of guessing.
- Catch only expected failures; return a clear `OperationOutcome`. The engine handles rollback.

## Tests

- Use `FakeRegistryStore`, `FakeProcessRunner` and `FakeOptimization` from
  `tests/ValorantOptimizer.TestSupport`. Never test by changing the real machine.
- Cover: assessment states, capture content, apply writes, restore (including values that were
  absent), unsupported/review paths, and preservation of unrelated data.
- Tests target `net10.0-windows`; xUnit. Run:
  `dotnet test tests/<project> -v q`.
- Every module must be constructible without administrator rights and must not perform writes
  during construction or assessment.

## Module authoring checklist

- [ ] Stable id, clear `Summary` / `WhatItChanges` / `Why`, honest `Compatibility`.
- [ ] `AssessAsync` explains current state + evidence (labels are shown in the UI).
- [ ] `CaptureAsync` covers every value that `ApplyAsync` changes.
- [ ] `ApplyAsync` is idempotent and returns a message describing the change.
- [ ] `RestoreAsync` restores exact existence, kind and data.
- [ ] All writable registry locations are in `ManagedScopes`.
- [ ] Tests cover apply/restore and the "not present originally" case.
- [ ] No admin self-elevation; `RequiresElevation` set when needed.
