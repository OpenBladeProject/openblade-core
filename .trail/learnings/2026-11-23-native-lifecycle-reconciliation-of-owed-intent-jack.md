---
title: Native lifecycle reconciliation of owed intent
author: Jack
date: 2026-11-23
branch: feat/native-power-lifecycle-reconcile
touches:
  - docs/windows-native-thermal-mutation.md
  - tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj
  - tools/openblade-windows-host/src/windows/scm/installed/readiness.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/request_triggers_low_power.rs
  - tools/openblade-windows-host/src/windows/scm/installed/request_triggers.rs
  - tools/openblade-windows-host/src/windows/scm/installed/status_source.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.NativeLifecycleReconcileVectors.cs
  - tools/openblade-windows-host/src/windows/scm/installed/lifecycle_triggers.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/request_triggers_lifecycle.rs
---

Native lifecycle reconciliation (Stage 3 C3, plus C1 power-kind and C2 display-off parity) supersedes the "two debt drops remain" part of 2026-11-22-native-low-power-trigger-deferral-second-jack.md.

## Decided
- The serving loop's 500 ms origin tick runs `StatusSource::reconcile_lifecycle` -> `Runtime::reconcile_lifecycle` (lifecycle_triggers.rs). It reconciles only the latest admitted reporter's pair through `reconcile_locked`, on three causes: a moved gate generation (resume, new resume, gate-clearing DisplayOn; owes nothing, runs an existing debt), a changed Windows power kind (owes every class), and an expired 15 s lease with an app active (clears foreground, owes every class). In low power it records the cause and reads nothing.
- The tick uses `gate.try_lock()` and skips when busy; its first tick only marks. A boundary renews the lease because Windows `Instant` counts sleep.
- Drop 2 (a fence that does not move the gate generation refusing a step) is recovered: a status-2 step with nothing possibly submitted while `Lifecycle::active()` moved is marked transient, so the debt stays owed with a retry spent (`Runtime::admission_epoch`). Unwired probes (rig default) keep the old behaviour.
- Drop 1 (possibly submitted step unfinished at the bound) is deliberately kept: owing it again would re-run an unknown submission; it recovers only via a later power-kind change, lease expiry or changed report after the fence settles read-only.
- lifecycle-reconcile-vectors.json is recorded from managed (`NativeLifecycleReconcileVectors`); native diverges on two rows, asserted in request_triggers_lifecycle.rs: lease expiry (managed submits nothing) and resume on an unchanged source after a deferral (managed submits nothing until convergence).

## Tried and ruled out
- Treating managed's leaseExpired row as a race: `ObserveAsync` does wait on the operation gate. Managed really submits nothing, because `PublishApplicationContextStatus` publishes the new desired state before `ApplyFullTransitionAsync` reads `current.DesiredState` as priorDesired, so `CompareTransitionAutomaticPerformance` sees OpenBlade's own Game preset as external and pauses performance and fans. Leaving a rule by report behaves the same in managed; native already writes there.
- Native's readiness fixture saves battery performance 3 (BatterySaver), not Silent; the C# recorder must match it.

## Watch out
- Git Bash `sed -i` with a single substitution strips CR from CRLF files here; renormalize docs and C# with `sed -i 's/\r$//; s/$/\r/'` afterwards and check `file`.
- `dotnet test --no-build` after regenerating vectors compares against the stale copy in bin; build again before verifying.
- `service-profiles-v1` lane tests `expired_refresh_discards_old_evidence_and_prevents_overlapping_reads` and `tfc_release_a_fenced_cancel_near_journal_capacity_keeps_its_reservation` failed once under load and passed alone.
- Not validated across a physical sleep/resume; a locked session at resume consumes the boundary and leaves the debt to the next report.
