---
title: "Native function-row review: suspend bursts, Enum.TryParse parity, stop order"
author: Jack
date: 2026-11-21
branch: feat/native-agent-function-row
touches:
  - docs/windows-native-agent-function-row.md
  - tools/openblade-windows-host/src/session_native/function_row/admission.rs
  - tools/openblade-windows-host/src/session_native/function_row/host.rs
  - tools/openblade-windows-host/src/session_native/installed_monitor.rs
---

## Decided
- Every tokio interval in the native Agent that re-verifies identity (function-row renewal, status-subscription idle check) uses MissedTickBehavior::Delay. Windows Instant counts sleep time, so the default Burst replayed 57,600 checks back to back after an 8 h suspend on the Agent's only runtime thread. Paused-clock tests advance 10 min and 8 h and expect exactly one call.
- The function-row preference follows Enum.TryParse exactly: skip leading char.IsWhiteSpace, a digit or sign starts one integer with only trailing ASCII white space (never comma-joined), otherwise OR trimmed comma-joined names. The shared vectors' preference section is recorded through UiPreferenceStore.LoadInputPreferencesAsync, so managed decides each value.
- Stop ordering lives in one Signals::retire (invalidate, then signal the supervisor); the dropped-future guard shares it. Tests read the stop flag at invalidation and record what the hook held when stopped.

## Tried and ruled out
- Separate Invalidate and Retire drop guards: locals drop in reverse order, so the dropped-future path signalled the stop before invalidating.
- Treating the stop-event name as a managed/native singleton: managed CreateShutdownEvent opens an existing event without failing; its real singleton is the session pipes.
- Wall-clock bounds on the hook callback in unit tests; count heap operations instead.

## Watch out
- windows/scm/installed/readiness.rs has the same default-Burst 500 ms origin-check interval; it was out of scope for this change.
- Admission freshness is a lease on the open subscription, not status age; expiry is the later of last status + 2 s and last renewal + 1 s.
- Driver mode gates press admission, not hook installation. The first press of each key after every install passes through.
