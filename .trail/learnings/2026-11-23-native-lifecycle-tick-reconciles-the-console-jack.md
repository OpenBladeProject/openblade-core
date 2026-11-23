---
title: Native lifecycle tick reconciles the console user with pending retries
author: Jack
date: 2026-11-23
branch: feat/native-power-lifecycle-reconcile
touches:
  - docs/windows-native-thermal-mutation.md
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.NativeLifecycleReconcileVectors.cs
  - tools/openblade-windows-host/src/windows/scm/installed/lifecycle_triggers.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/request_triggers_lifecycle.rs
  - tools/openblade-windows-host/src/windows/scm/installed/request_triggers.rs
  - tools/openblade-windows-host/src/windows/scm/installed/status_source.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_native.rs
  - tools/openblade-windows-host/tests/data/lifecycle-reconcile-vectors.json
---

Supersedes 2026-11-23-native-lifecycle-reconciliation-of-owed-intent-jack: an independent review refuted three of its parity claims.

## Decided
- The lifecycle tick's pair is the console session's interactive user (`Runtime::lifecycle_pair`, `thermal_native::console_user`): the latest reporter if it reported from the console session, else the console user with the Service pid as the steps' caller. Requiring a reporter made the feature dead by default, because no application rule exists by default and the Agent then never reports. The probe only names a candidate; `WindowsGuard::capture` proves session, unlocked, SID and LUID before any read or write.
- A cause stays pending (`lifecycle::Pending`) until an attempt passes `admit_trigger` and runs its steps. A refused attempt (no console user, refused capture such as a locked resume, fenced store, unknown power) or one the write epoch overtook is retried only when `Lifecycle::active()` moves, at most `RETRIES` more times. Ticks at an unmoved epoch take no capture.
- A tick gap of at least 5 s (`SLEPT`) renews the lease like a boundary does: Modern Standby can sleep with no PBT_APMSUSPEND, and `Instant` counts sleep.
- `Runtime::native` samples the Windows power kind once at start. The tick reads the generation with `observe_power_kind(None)`, and only a sample that differs from a known kind advances it, so without that baseline the first unplug after start was the baseline and was missed.
- `admit_trigger` (intersection, pending, health, authorize, health) is shared by the report path and the tick. `APPLICATION_LEASE` is defined once in thermal_mutation.rs. The status snapshot is cloned only for an attempt that runs.

## Tried and ruled out
- Clearing the cause after any attempt: a locked resume dropped the power change, and it waited for the next report.
- Sampling the power kind on every tick: it would add a fencing source between a capture and its write. A single start-up sample is enough.

## Watch out
- Native has no drift tracker. A power-kind change or lease expiry overwrites an out-of-band performance or fan change; managed excludes those subsystems (ServiceControlRuntime.cs:16655-16694). This is documented as a third divergence against Architecture rule 5 and must be resolved before packaging.
- `resumeUnchangedAfterDeferral` in lifecycle-reconcile-vectors.json models only Enter/Exit plus HandlePowerSourceChangedAsync, not the full ProcessNativeAsync or convergence.
- A GuardedStatus tick does nothing until the status feed has a publisher, which the first status read creates. Tests without a report must call `rig.get()` first.
- In the rig, `refuse_at = 2` refuses the step's first authorize, because the tick's `admit_trigger` is #1. `scoped_fresh` with `current_console = false` refuses the factory capture while the console probe still names a pair.
- A lock-screen resume can spend the retries on several epoch moves while still locked; the debt is then left to the next report.
