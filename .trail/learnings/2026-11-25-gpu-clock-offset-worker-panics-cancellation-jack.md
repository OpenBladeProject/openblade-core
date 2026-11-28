---
title: "GPU clock-offset worker: panics, cancellation and the paired-write lane"
author: Jack
date: 2026-11-25
branch: feat/native-gpu-clock-tuning
touches:
  - docs/windows-rust-development-service.md
  - tools/openblade-windows-host/src/windows/scm/installed/gpu_clock_offsets.rs
  - tools/openblade-windows-host/src/windows/scm/installed/gpu_clock_offsets_tests.rs
  - tools/openblade-windows-host/src/windows/scm/installed/gpu_clock_offsets_windows.rs
  - tools/openblade-windows-host/src/windows/scm/installed/nvml_library.rs
---

## Decided
- A job that panics on the gpu_clock_offsets worker is caught (catch_unwind) and the worker keeps serving, because its in-memory uncertainty record is the only one. Management::recover_from_panic shuts down an NVML session the panic left open and marks an interrupted submission (tracked by `submitting`) SubmissionUncertain. A panic outside a submission leaves the state as recorded.
- The caller of a panicked job gets Called::Abandoned, never NotStarted: the reply drops while the claim is STARTED.
- `call` is cancel-safe: a Claim guard moves QUEUED to WITHDRAWN on drop, so a cancelled call never runs later unobserved.
- `submit` rechecks the session-free rules (well-formed, reset for a relinquish, envelope), and `mark_reset_confirmed` returns false unless a default reset is still confirmed. Neither replaces the exclusive thermal lane the integration must hold from preflight to completion, as managed EnterHardwareOperationAsync does.

## Tried and ruled out
- Poisoning the worker after a panic (every later call Unavailable): it would discard the uncertainty record that only this worker holds.
- Marking every panic uncertain: a panic in a read-only job would then turn foreign offsets into Managed on the next resolve.

## Watch out
- The reply drops during unwinding, before recover_from_panic runs; assert recovery effects only after the next serialized call.
- A mutation runner must restore from a saved copy, not `git checkout`, while fixes are uncommitted.
- Native resolves all seven NVML exports at load: a missing library or export reports status 12 (managed: the .NET load exception), and a driver without the setter is Unavailable at preflight (managed: KnownNotApplied at the set).
- A bare-name LoadLibraryExW returns any already-loaded nvml.dll, so the future trust gate must verify the loaded module path.
