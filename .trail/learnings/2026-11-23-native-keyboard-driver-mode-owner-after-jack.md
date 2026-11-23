---
title: Native keyboard Driver-mode owner after the third review round (supersedes the delta-review learning)
author: Jack
date: 2026-11-23
branch: feat/native-driver-mode-acquire
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/driver_mode_owner.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/driver_mode_owner.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/device_mode.rs
  - docs/windows-rust-development-service.md
  - ".github/workflows/rust.yml"
---

This supersedes `2026-11-23-native-keyboard-driver-mode-release-after-jack-2.md` after the third review round (E1-E7). Where the two disagree, this one holds; its other decisions stand.

## Decided

- The owner step is due every `INTERVAL` (15 s), as managed's. READ_LEAD is gone. The L3 skip stays: after the startup call, a step starts no job only when a client's ObserveState read job recorded Driver under the current read admission within the cadence. Subscription refreshes do not run the read job, so otherwise a due step on a verified device runs one mutation job: on Driver, one 00/84 exchange plus a reader stop and restart and their fresh admissions, every 15 s. With no ObserveState in the last 5 s the device is unverified and no step job runs. No new read loop was added.
- An unverified device still re-captures on an epoch move (E1): the unverified branch resets the latch and calls `capture_console` when `moved`. Otherwise a hidden Tray, with no ObserveState, left the release on a stale epoch and refused it.
- An Indeterminate owner captures only on an epoch move, not at each due step (E7).
- A capture refused for a cause that clears without a notice (for example a competing controller that exits) is retried only at the next epoch move or the next due step on a verified device.
- `released()` no longer clears `released` on a Pending job: nothing calls the release twice. The lane-refusal arm still does, pinned by `a_release_that_starts_no_job_keeps_its_attempt`, though production starts the release only at Stop.
- The healthy-release figure is about 0.28 s by count besides its three exchanges (about 22 admissions at 8-9 ms plus three 30 ms settles). It is an estimate pending an attended RZ09-0581 timing.
- Known gap, joining the deferred low-power/D1 slice and still blocking packaging: Windows closes applications and logs off every user session before it notifies services. Microsoft Learn documents that order for the full shutdown that Fast Startup begins with ("Preparing for fast startup"); the SCM then sends PRESHUTDOWN and SHUTDOWN ("Service Control Handler Function"). With Fast Startup on, a shut down hibernates the system session, so services get no SHUTDOWN at all. Under console-user write admission (decision 1) the shutdown release is refused at admission, or never runs, and Driver mode stays on. That slice must design a shutdown and low-power release admission that does not depend on a signed-in console user. Nothing was run to observe this.

## Tried and ruled out

- The previous learning's equivalent survivor "job-deadline `min(end)`" is wrong: it is a real bound. A 600 ms owned-Stop authorization (`owned_stop_stall`) plus 100 ms exchanges gives Exhausted with no setter (`slow_admissions_leave_the_release_job_only_the_rest_of_the_share`); without `min` the setter goes out.
- The `release_once` fatal arm is no longer an equivalent survivor either. With `fatal_health` set on the readback query, the lane's retirement reports the fatal and nothing retains it, so only that arm fails the release (`each_release_fatal_backstop_holds_on_its_own`).
- Pinning `released()`'s `verify_retained_failure` through `on_mutation_retired` does not work: the guarded lease's check in `drain_mutation` catches it first. Set the retained evidence on the readback query and delay retirement past the share (`device_mode_retire_delay`), so only `released()` sees it.
- Remaining equivalent survivors under the fixtures: dropping the retained check in `refused()` and dropping the health check in the fixture's owned-Stop authorization.

## Watch out

- `owned_stop_stall: (n, d)` sleeps in the nth owned-Stop fresh authorization. n = 9 is the trailing checkpoint after the baseline reply. It pins the pace's trailing charge (`device_mode.rs`, `slowest + trailing`): with it the release is Exhausted, without it Restored.
- `device_mode_on_query` numbers: the release baseline is the current query count + 1 and the readback + 2.
- CI: `$driverModeOwnerTriggers` (keyboard + profile triggers) is the only lane that compiles the readiness tick's keyboard-plus-triggers path; it also runs `tfc_release_a_cancel_whose_port` under temporary-charge and `pool_pins` under fan-v2.
- Clippy failures in the read-v1-only and live-readings-only lanes predate this branch.
