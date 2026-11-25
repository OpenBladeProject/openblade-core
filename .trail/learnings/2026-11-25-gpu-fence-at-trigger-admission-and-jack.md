---
title: GPU fence at trigger admission and its power rearm
author: Jack
date: 2026-11-25
branch: feat/native-gpu-thermal-integration
---

## Decided
- A pending GPU clock confirmation (the lane's outstanding call or uncertain submission) is checked at trigger admission, after the journal fence and fresh authorization and before any firmware read: `admit_trigger` returns `TriggerAdmission::ThermalPending` and the debt is left as it was. Profile saves, lifecycle attempts (Attempt::Refused, cause kept), convergence (before the reservation, and a due D7 retry retired Blocked unrun) and `reapplyChangedSettings` (managed's PENDING reply, as `pendingWriteConfirmations.HasPending`) use the same `Runtime::gpu_fenced`.
- `Runtime::gpu_fenced` runs the fence on `spawn_blocking`: the trigger paths are async on the Service runtime and the fence may make one NVML read of up to 1 s. A join error fences.
- The writer fence now carries a `Boundary` (gate and Windows power generations, no source) captured when its permit is built (`Runtime::gpu_fence`), and `Lane::fenced` crosses it before its read, so a power change rearms the one ungated query for the next writer or trigger. This supersedes the 2026-11-25 learning that the fence crosses no boundary.
- `cross` keeps the last seen source when a boundary carries none, so a fence between two steps at one class rearms nothing. A PluggedIn to USB-C change within one Windows power generation is still rearmed only by the next explicit performance step (documented deviation).

## Tried and ruled out
- Adding `Cause::ThermalPending` to the performance step's transient set: a long fence would exhaust `RETRIES` and settle the debt anyway.

## Watch out
- `fake.sets()` already counts the failed set that `leave_uncertain` makes; compare against the count after it, not 0.
- Readiness tests should read the fence through `rig.runtime.gpu_fence()` (helper `fenced(&rig, budget)` in gpu_clock_tuning.rs), which works on both old and new code, so the old-commit check compiles with only test files swapped.
- A convergence evaluation needs a fresh reading after the fence clears: drift or observe again before the tick.
- The fenced replies no longer carry the fan step's "Automatic mode independently observed..." suffix, because no step runs.
