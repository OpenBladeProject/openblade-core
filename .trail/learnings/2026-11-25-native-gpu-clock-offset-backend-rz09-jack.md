---
title: "Native GPU clock-offset backend (RZ09-0581, no op wired)"
author: Jack
date: 2026-11-25
branch: feat/native-gpu-clock-tuning
touches:
  - ".github/workflows/rust.yml"
  - docs/windows-rust-development-service.md
  - tools/openblade-windows-host/src/windows/scm/installed/gpu_clock_offsets_tests.rs
---

## Decided
- The native NVML clock-offset backend (gpu_clock_offsets.rs) ships first with no request wired; setGpuClockTuning and the performance integration that replaces Storage::gpu_incomplete come after the thermal-recipes slice, because managed commits GPU tuning into the shared performance override.
- No durable journal: an uncertain submission lives in memory, as managed does. After restart the state is Unknown and the next write query reads the driver. This is parity, not a gap.
- nvml.dll loads through one System32-only loader (nvml_library.rs) shared with the temperature reader, with no signer check, like managed DllImport. A WinVerifyTrust plus catalog-membership check belongs in nvml_library::load and gates packaging any GPU writer.
- Stop, shutdown and low power write nothing; offsets stay in the driver, as managed.
- A driver reading outside the admitted envelope resolves to Managed with no confirmation (PR 991 rule), via Policy::clip.

## Tried and ruled out
- Wiring setGpuClockTuning now: any native GPU commit trips gpu_incomplete and blocks every thermal path until the performance runtime understands GPU halves.
- A durable uncertainty journal: managed has none, and Unknown plus the ungated query already resolves a restart.

## Watch out
- The uncertain_prior guard in submit is reachable only through a submit without its own preflight (the paired path); a dedicated test pins it, since set() alone never exercises it.
- The worker's claim (QUEUED/STARTED/WITHDRAWN) decides NotStarted versus Abandoned; Abandoned must map to Indeterminate for writes.
- cargo fmt on this crate reformats readiness_tests/request_triggers_lifecycle.rs (pre-existing drift); revert it rather than committing it with unrelated work.
- The controller vectors (gpu-clock-offset-vectors.json) are recorded by NativeGpuClockOffsetVectorTests; the management machine has no recorded vectors, only unit tests of the managed rules.
