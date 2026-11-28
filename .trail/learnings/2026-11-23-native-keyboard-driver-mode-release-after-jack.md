---
title: Native keyboard Driver-mode release after review (supersedes 6a-3 learning)
author: Jack
date: 2026-11-23
branch: feat/native-driver-mode-acquire
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/driver_mode_owner.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness.rs
  - tools/openblade-windows-host/src/windows/scm/installed/status_source.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/device_mode.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/windows.rs
---

This supersedes `2026-11-23-native-keyboard-driver-mode-ownership-stage-jack.md` (stage 6a-3) after its review round. Where the two disagree, this one holds.

## Decided

- The shutdown release now matches managed `ReleaseDriverModeCoreAsync`: a fresh 00/84 read of Driver under owned-Stop admission writes `00/04:0000` once, whoever set Driver mode. In-memory ownership (`owns`) only tells a read of Normal apart as a foreign change, plus diagnostics. A process with no console capture, or with a recovery-only read lease, still cannot write.
- Owned-Stop admission for the release is the same as `Runtime::stop_fans`: the capture's own, or `Fresh::capture_settle` (same SID, session and logon, current epoch) after a power or session notice moved the epoch the capture pins. The settle capture authorizes this one Normal write; the fan path uses it only to read and append. No Service-scoped authority was added.
- Shutdown budget: the keyboard gets 0.5 s in total (`SHUTDOWN_BUDGET`, gate and `drain_active` waits included). `SOURCE_DRAIN_TIMEOUT` = `DRAIN_TIMEOUT` + 0.5 s, so fans keep their 2 s. Listener 2 s + source 2.5 s = 4.5 s, inside the 5 s SCM wait hint. The acquire job has its own 2 s budget from job start (`Permit::budget`), after the lane wait.
- Keyboard release outcomes are a `Release` value kept on the Runtime, never an error. Only fatal admission evidence fails the Stop.
- `DeviceModeStep::Pending` (refused before any firmware command, including a write port that failed to open) records nothing and spends no recovery. It must not publish null over the read job's result.
- After the startup call, a step skips the job when the read cadence already holds Driver for the current admission. The first tick always reads.
- No keyboard retry after the fan handback: by then `drain_active` has joined every fan job, and `stop_fans` refuses a pending fan row and never settles it. So the guardian block managed's retry waits on can never clear here.
- A mutation port that fails to open (`windows::mutation_worker`) returns `Ok(None)`, known not applied, instead of an error that fenced the lane. Only the RZ09-0581 native worker is affected.
- `service-keyboard-driver-mode-v1` now implies `service-fan-mutation-v2`, not `service-profile-triggers-v1`. The low-power and console probes are cfg'd for either feature.

## Tried and ruled out

- Using `fresh_refuse` to drive a failure streak in owner tests. It now gives Pending, which by design spends no recovery. Use `device_mode_mute` (every 00/84 times out) for Unread failures.
- Testing a step after a confirmed Driver with only `driver_mode_due_for_tests`. The read-cadence skip finds the fresh record and starts no job. Age both the owner and the cadence (`later()` in the owner tests).
- Testing the fan-guardian block through another user's pending fan row. The capture itself refuses cross-user then. Instead, hold a fan job on `held_read` and drop its request, which leaves a not-joined job with no pending journal row.

## Watch out

- With `refuse_at`, the first two fresh authorizations of a step are its admission and the lane's. The job's first checkpoint is authorizations 5 and 6, so `refuse_at = 5` refuses before the first send. 1-4 refuse before the job starts (Pending).
- A worker error leaves the lane unconfirmed. `confirm_drained` then fails, so a test must call the release directly (`Unsettled`) rather than drain.
- The release ID and acquire IDs are still unjournaled until 6a-5, and the low-power boundary release plus the D1 fan handback still block packaging.
