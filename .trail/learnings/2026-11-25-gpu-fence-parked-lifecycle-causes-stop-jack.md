---
title: "GPU fence: parked lifecycle causes, Stop-time permit fences, exit-recipe composition"
author: Jack
date: 2026-11-25
branch: feat/native-gpu-thermal-integration
---

## Decided
- A lifecycle cause refused by `TriggerAdmission::ThermalPending` is parked (`Pending::gpu_pending`, `Attempt::GpuPending`): each tick re-checks the GPU fence while it is parked and runs the cause once the fence clears, at an unmoved epoch too. Clearing the fence moves no write epoch, so `Pending::wait` alone left the battery recipe unapplied on a default install with no reports. Any other refusal (`wait`) clears the park.
- Owned Stop's settle-only and handback permits carry a fence built at Stop (`Runtime::gpu_fence` in `run_owned_stop`), not the owner request's. The owner's own capture keeps its request-time fence: it authorizes only inside its lifecycle epoch, which every power notice moves.
- `Runtime::gpu_fenced` passes a zero budget in low power, so a trigger's fence starts no GPU call there (and no `cross` with a `None` gate). `reapplyChangedSettings` had no gate check before its fence.
- Merged with the HyperBoost exit (38b93062): the fence check stays after `resolve` and before the plan, so the restore recipe's four setters are refused with every writer; `submit_setter` charges `permit.reserve()`; `prepare_gpu` checks `Plan::NoDirectPath` only without the exit feature. GPU clock tuning adds no progress values; the exit owns 80-83.

## Tried and ruled out
- A settle-only Stop test in the GPU rig: `settle_permit_for_tests` returns None there (no settle capture), and Stop never closes the owner through settle, so the settle fence change is covered only by symmetry with the handback test (mutant S2 survives).
- A pace check on the parked release: equivalent, since the refused attempt itself ran only after `PACE` and parking keeps `stepped`.

## Watch out
- Mutant runs filtered with `-- gpu` miss tests whose names lack "gpu" (the parked-cause refusal test); filter by the test's own words.
- In the convergence rig a `power_notice` without a kind change moves no power generation: use `unplug` to make the fence cross a power change.
