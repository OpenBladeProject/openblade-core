---
title: "Native performance shortcut: safe-default start and preview witness"
author: Jack
date: 2026-11-25
branch: feat/native-thermal-recipes
touches:
  - docs/windows-native-thermal-mutation.md
  - tools/openblade-windows-host/src/windows/scm/installed/performance_shortcut.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/performance_shortcut.rs
  - tools/openblade-windows-host/src/windows/scm/installed/status_source.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
---

## Decided

- The performance shortcut's classified cycle starts from managed `Automation.Resolve(..).Performance`. With no profile aggregate stored, managed resolves `ProfileDefaults.CreateSafeDefaults`, so `Storage::selected_performance` falls back to `profile_thermal(power)`. It does this only when no override applies and no profile resolution was read. `profile_thermal` is the same safe-defaults path the combination check uses. Without the fallback, a fresh install's first press on a classified adapter "selected Balanced", wrote nothing, and every later press ran one preset behind managed.
- The preview publishes through `publish_preview`, which is `current_policy` plus a revision bump. It does not use `publish_reconciled`, which calls `reader.retain_mutation` and could re-stamp the Reader's read witness and extend its retention window. A preview reads nothing.
- One `ProfileLayer` decode per request step: `resolve` and the low-power deferred store decode it once, ahead of the shortcut staging, and reuse it for the destination.

## Tried and ruled out

- Exercising "Windows battery while 07/8C reads an external class" through the dispatcher. The thermal worker refuses a performance request there before `resolve` (`Power::thermal_source` needs AC presence for an external class). The refusal comes when `admit` sees the reading, after the same 0D/82 (twice) and 07/8C queries an explicit temporary Battery Saver sends. Both are refused identically, and nothing is stored or written. `stage` still resolves under the cycle's source (class 1 for the battery cycle), as managed does, but no admitted path makes that differ from the adapter class, so its mutant survives as equivalent.
- A non-retiring variant of `Storage::selected` for that case: unnecessary, because the staging class always equals the class `observe_power` already retired at.
- A managed vector for the no-aggregate cycle: the recorder covers `PerformanceShortcutSelection` only, and the resolution is not a selection question.

## Watch out

- With the safe defaults, Battery Saver selects Battery Balanced, the same preset "not in cycle" selects, so the battery no-aggregate case cannot tell the fallback apart. Only the adapter cases (Balanced to Silent) can.
- The witness mutant (retaining on a preview) survives through the dispatcher, because `serve_mutation`'s pre-mutation snapshot publication carries no fresh read witness. A dispatcher test cannot fail against it, so there is none: the preview witness rule is held by `publish_preview` and the guide's statement. A test that kills it would have to call `publish_preview` over a publication that carries a valid read confirmation.
- When the GPU clock writer lands, stage the resolved desired tuning as managed does (`current.DesiredState.GpuClockTuning`), not only the override's.
