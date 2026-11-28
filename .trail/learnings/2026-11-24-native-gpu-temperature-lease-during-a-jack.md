---
title: "Native GPU temperature: lease during a sample, Stop cancels, reading order"
author: Jack
date: 2026-11-24
branch: feat/native-gpu-temperature
touches:
  - docs/windows-rust-development-service.md
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/windows/scm/installed/gpu_temperature.rs
  - tools/openblade-windows-host/src/windows/scm/installed/gpu_temperature_tests.rs
  - tools/openblade-windows-host/src/windows/scm/installed/live_readings_tests.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
---

Supersedes "Native GPU temperature: managed gate, worker and NVML load" on the lease, Stop, the reply wait and the NVML trust choice. Everything else there still holds.

## Decided
- The lease is enforced during a sample, not only between requests. A sample first ends a session whose lease ends within `SAMPLE_BUDGET` (250 ms: the 150 ms workload window plus the D-state read), and the D-state read ends one whose lease ran out during the Windows reads, before NVML is called. Only a stalled PDH or SetupAPI read can still hold a session past 5 s; managed's timer thread is stricter. A full timer thread was judged too large for the gap.
- Stop is a cancellation: `Host::stop` sets an `Arc<AtomicBool>` that the worker ORs into every cancellation check, so no NVML call follows the one in progress and a queued request is skipped. `NvmlSession` ends a started session on drop, which is how the worker's exit ends it.
- `Pending::temperature` waits at most `AFTER_READING` (250 ms) once the reading has arrived, and never past deadline minus `RESERVE`, so a slow sample holds neither the reply nor the serial lock.
- Without thermal mutation nothing orders observations, so an observation waiting for its GPU sample could overwrite a later job's reading. `ReadSource` keeps the start instant of the job behind `last` (`newest_job`) and drops an older job's reading; the instant is taken right before the worker job, and jobs run one at a time. The mutation lock was deliberately left conditional.
- NVML stays loaded from System32 only, without a signer check, for now. The signer check is queued with the package import-allowlist review, which must also admit pdh.dll and dxgi.dll.
- NVML, PDH and SetupAPI failure statuses are not kept: no diagnostics producer would read them. The docs record the gap.
- The LiveReadingSensors bits live once, in `service_status::live_sensors`.

## Tried and ruled out
- An `expire()` right after the workload read as well as after the D-state read: the second covers the first, and the worker's `recv_timeout(0)` covers a cancellation in between, so the mutant was equivalent and the call was removed.

## Watch out
- A test that drops a `Pending` cancels its sample: the worker sees the closed reply as abandoned. Keep it alive until the calls you assert have happened.
- `Pending::temperature` now returns None after 250 ms even when the request's own time is far off; a held-sample test that awaits it gets None, not the value.
