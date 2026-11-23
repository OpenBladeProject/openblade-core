---
title: Native keyboard Driver-mode release after the delta review (supersedes the review-round learning)
author: Jack
date: 2026-11-23
branch: feat/native-driver-mode-acquire
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/driver_mode_owner.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/battery_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/device_mode.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/driver_mode_owner.rs
---

This supersedes `2026-11-23-native-keyboard-driver-mode-release-after-jack.md` after the delta review (D1-D9). Where the two disagree, this one holds.

## Decided

- A settle capture no longer authorizes the keyboard's 00/04:0000. `Fresh::capture_settle` returns `Arc<dyn Settle>` (owned-Stop reads and health only), so the type system keeps it out of `DriverModePermit`. Instead the owner tick re-captures ordinary admission whenever the lifecycle write epoch (`Lifecycle::active`) moves, between steps and after an Indeterminate step. `capture_console` records the epoch it read even when the capture is refused, so a refusal retries at the next epoch move or due step, not every tick.
- The release has a 0.9 s share (`SHUTDOWN_BUDGET` = 150 ms entry + 700 ms job + 50 ms retirement slack, const-asserted). The outer `timeout_at` ends the release wherever it is, including the lane's post-deadline join; a job slot lets an exhausted share still read the job's step. `SOURCE_DRAIN_TIMEOUT` = 2 s + 0.9 s; listener 2 s + source 2.9 s = 4.9 s, inside WaitToKillServiceTimeout (native accepts SHUTDOWN, not PRESHUTDOWN). The healthy-release figure (0.43-0.57 s) is an estimate that still needs attended RZ09-0581 timing.
- The next due step is INTERVAL + READ_LEAD (5 s) after the last, so the read job's own Driver result leads it.
- DeviceModeStep: Unread is recorded only right before the baseline send; a pace refusal records Pending. The pace charges the readback's trailing checkpoint (`setter_fixed` = slowest + time since the last reply).
- A refused owned-Stop authorization is checked again for fatal evidence (`Runtime::refused`): an authorization returns `None` even when custody integrity was lost, which otherwise reported `Unadmitted` and swallowed the fatal.
- `mutate_cleanup` keeps a fatal job result ahead of the unconfirmed-stop drain error, as `mutate` does.
- Temporary-charge cancel: a cancel that sent nothing at an unmoved epoch while its fresh admission still holds (write port failed to open) rolls back its claim and clears the coalescing mark, at most `UNSENT_CANCEL_RETRIES` (2) times, then pauses. A refusal at the tick's own epoch still pauses once and is never retried.

## Tried and ruled out

- Checking `verify_retained_failure` at `release_once` entry: it fails the Stop in `edge_original_owner_integrity_loss_after_retirement_never_reopens_reads`.
- A same-target fan request on `held_write`, dropped: its journal stays pending ("Fan submission remains unresolved at Stop"). Use `held_read` on 0D/82.
- Killing the `release_once` fatal arm and the job-deadline `min(end)`: both are equivalent under the fixtures. GuardedReadCustody shares the Runtime's fatal slot, so a lane fatal before the job is also caught by the Pending arm; ENTRY + JOB equals SHUTDOWN − SLACK, so `min` only clips wall time spent in synchronous admissions.

## Watch out

- The test custody's `integrity_lost_at` loses integrity at the nth check. In the release, checks 1-6 are the health check, the owned-Stop authorization, the lane's integrity check, the lane's two authorizations and the job's first checkpoint; checks 2 and 4 refuse as `None`.
- The fan rig needs `rig.origin.stop()` before a release can pass owned-Stop admission.
- The scratchpad is shared with the ob-drift agent: never reuse generic script names such as `lanes.sh`.
- The lifecycle reconcile still reports an unsent refusal only in `Reconciled.message`, with no retry until the epoch moves (left unchanged to avoid request_triggers churn).
