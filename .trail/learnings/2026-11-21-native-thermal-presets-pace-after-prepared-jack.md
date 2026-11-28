---
title: "Native thermal presets: pace after Prepared, fan checks, deferred handbacks"
author: Jack
date: 2026-11-21
branch: feat/native-thermal-presets
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/readiness/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness/fan_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
  - rust/crates/openblade-domain/src/fan_recipe.rs
---

## Decided
- Native thermal and fan recipes check their pace after the Prepared row (0) and before the first setter. They pace at the slowest exchange interval since `Pace::start` (or at the interval still open, if that is longer), not the mean. A refusal settles the row as 6 and leaves nothing pending. Owned Stop cleanup is not paced. Refused recipes do not return their transaction IDs: the pool is never reclaimed within a process.
- Before storing, native fan requests run managed's `FanControlPowerPolicy` check and then `ValidateThermalOverrideCombination` against the current performance override, which is the persistent one or else an unexpired temporary. A rejection replies status 3 and writes and stores nothing.
- An unclassified reading blocks fan and performance requests before storing, as managed's `CanAdmitUnclassifiedExternalControl` does.
- A profile Custom without levels resolves to `CustomPerformance::DEFAULT` (Medium/High) via `thermal_storage::profile_custom`, as managed's `CustomPerformanceSettings.Default` fallback does.
- Temporary-charge mutation always gets `RECIPE_BUDGET`. Its deadline is the bound: 6 s for requests and 4 s for the automatic release.

## Tried and ruled out
- Pacing on the mean time per exchange. One slow 07/8C read, or a 1.6 s journal append, passed the check and then hit the write fence partway through the recipe.
- Saying selectors 5/4/3/6 "have no plan" as a parity fact. Managed does hand manual fans back there (`ProductionBatteryPort.cs:1278-1323` writes the fan state command under the observed selector with fan 0). Only native lacks the plan, and that handback is deferred.

## Watch out
- With GPU tuning configured, native leaves manual fans under selectors 5/4/3/6 by neither request: the fan return has no destination, and a performance request under manual fans is deferred.
- Not ported: managed's confirmed-state Custom fallback, and the confirmed-state-based fan combination check on unclassified power. A battery-power Fixed fan request is stored but not applied (status 2), because native has no battery-selector fan recipe.
- Slowest-interval pacing is more sensitive to scheduler stalls. A loaded machine produced spurious Deadline refusals in one test run; the rerun passed.
- This supersedes parts of 2026-11-21-native-thermal-presets-custom-every-fan-jack.md: the pace position, the fan checks, Custom without levels, and the "no plan" wording.
