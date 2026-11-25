---
title: "GPU clock half: request deadline bound and shared thermal fence"
author: Jack
date: 2026-11-25
branch: feat/native-gpu-thermal-integration
---

## Decided
- Every GPU clock call (`thermal_gpu::Lane::call`) is bounded by the request: what remains of the deadline less `REPLY_MARGIN` (500 ms), capped at `CALL_BUDGET` (5 s). A paired step reserves `COMPLETE_RESERVE` (1 s) of the pace through `Permit::reserve`. A started NVML call cannot be withdrawn, so an abandoned one replies Failed and indeterminate and stays counted in `Outstanding.calls` until it ends on the worker.
- The managed pending-confirmation block is a shared Reader gate, `Permit::thermal_fenced`, checked after the destination or fan plan is chosen and before any row or setter (`mutation.rs` apply, both branches of `fan.rs` apply_fan). `Runtime::gpu_fence` hands the lane's `fenced` to RequestPermit, FanPermit, and through the owner to the settle and handback permits. Refusals use managed's two texts: `PENDING_PERFORMANCE` for explicit performance steps and `PENDING_OTHER` for everything else.
- The fence does not live in `Storage::pending`, because that store is per user and also blocks saves and battery writes.
- Status follows managed: GPU Indeterminate and WriteRejected results are Failed (3), CapabilityUnavailable is Blocked (2), and each keeps the committed prefix.
- `Boundary.source` carries the resolved power class, so a change between classified sources (PluggedIn and USB-C) rearms the one ungated query.

## Tried and ruled out
- A per-path fence in fan_mutation or the trigger code was ruled out. The coordinator asked for the shared layer, and the Reader is the one place every thermal writer passes through.
- Using `mark_uncertain_for_tests` to fence readiness tests does not work: the fence's own read records the management's real state and clears it. Use a real failed submission (`leave_uncertain` in gpu_clock_tuning.rs) instead.

## Watch out
- The fence's resolution read crosses no boundary, so after a power or source change, fan and reconciliation writers stay fenced until the next explicit performance step takes the rearmed query. This is the safe side, and it is documented.
- Owned Stop's fan handback is refused while the fence holds. This matches managed, which blocks prior-authority cleanup.
- Rig selector 1 with Automatic fans makes trigger reconciliations fail admission in these tests. Use selector 0.
- `gpu_incomplete` still treats an override's tuning as a GPU effect on battery, where managed selects none. This is deferred to the second half.
