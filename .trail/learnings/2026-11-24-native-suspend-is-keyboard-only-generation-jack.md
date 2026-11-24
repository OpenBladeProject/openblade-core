---
title: Native suspend is keyboard-only; generation fence and Stop cleanup (supersedes the low-power release learning)
author: Jack
date: 2026-11-24
branch: feat/native-low-power-driver-release
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/low_power_cleanup.rs
  - tools/openblade-windows-host/src/windows/scm/installed/driver_mode_owner.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_native.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/windows/scm/installed/driver_mode.rs
  - tools/openblade-windows-host/src/origin_lifetime.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/fan.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs
  - docs/windows-rust-development-service.md
---

This supersedes `2026-11-24-native-low-power-driver-mode-release-jack.md` for the suspend fan handback, the low-power keyboard share, the D1 fence, release uncertainty, the release's journal check and the mutant reasoning on the Attempted check. Where the two disagree, this one holds; its cleanup authority boundary, keyboard release and Stop-time handback stand.

## Decided

- The suspend preparation releases the keyboard only and never touches the fans. AGENTS.md rule 1 names only the keyboard at a low-power boundary, and managed restores fans at suspend only for a Battery Saver fan stop it owns (`OwnsBatterySaverFanStop`, ServiceControlRuntime.cs:13069-13087, property at :21049). Native has no Fan Stop (openblade-domain profile_set/policy.rs). The old line range 13086-13101 was wrong. `FanHandbackPermit` refuses any scope but `OwnedStop`. TODO(fan-stop) in low_power_cleanup.rs: when Fan Stop lands, restore an owned Battery Saver fan stop at suspend as managed does, and do not copy the AC-only pre-check of the owned-Stop recipe (fan_mutation.rs).
- Keyboard share at suspend = min(remaining window, 900 ms). A low-power release waits on the gate and the join for that whole share (`Boundary::LowPower` entry = share end), as managed's `ReleaseDriverModeAsync` waits on its operation gate. The 150 ms entry is for shutdown only.
- D1 revised: `Lifecycle::fence_for` pins the suspend generation for `LowPowerCleanup` (tagged with bit 63 so it never equals an epoch) and the epoch for `Ordinary`. `CleanupGuard`, `ReadLease::authorize_cleanup` (via `low_power_admission`), the test custody and `CleanupFresh` all use it. A lock during the release no longer creates uncertainty. Owned Stop keeps the epoch fence.
- `Keyboard::uncertain` is now `Option<Instant>`. `maintain_driver_mode` resolves it read-only through `ReadSource::driver_mode_confirmed_since(since)`: a confirmed 00/84 read under the current read admission, from a job that started after the uncertain release. Driver keeps `owns`, any other mode clears it, and the same step then acquires through the normal cadence. Nothing is replayed.
- `thermal_native::admit_cleanup(verify, committed, fence, power_and_controllers, custody)` is the one cleanup admission order: verify, fence, committed, power+controllers, verify, fence, custody. `CleanupFresh` runs the same function, so the test model can no longer drift from the guard.
- Owned-Stop fan cleanup is paced before row 31 (`Pace::covers_before_rows`, every row including 31 charged at the slowest exchange). A 31/4x tail cannot be closed in process (51 follows only 30), so a Stop that leaves one reports "not independently confirmed" and the next start recovers it read-only. That predates the low-power slice.
- The cleanup admission (Cleanup, CleanupFactory, CleanupGuard, capture_cleanup, FanHandbackPermit and the run_owned_stop handback branch) is now composed under `service-fan-mutation-v2`. A fan-only build hands fans back with nobody signed in; CI runs `fan_v2_owned_stop_cleanup` in that lane. `CleanupScope::LowPower` and everything at suspend stay keyboard-only.
- The release's journal check reads the store of the latest capture of any caller (`Runtime::captured_store`, set in `capture_scoped`), not the last console capture. Captures are serialized, and every store recovers the one custody journal's last row when it opens.

## Tried and ruled out

- The earlier claim that dropping the `release.swap` Attempted check is a surviving-but-harmless mutant was wrong. A registered job can send reserved cleanup ID 15 (and 16, 17) in its baseline reads before any row (reviewer R8). With the per-owner `cleanup_registered` check in `run_owned_stop` removed, the handback admission resent 15, 16, 17. `fan_v2_owned_stop_cleanup_too_slow_for_its_recipe_writes_no_row` now kills that mutant.
- Restoring the owner's cleanup attempt when no job registered (a Drop claim) was removed. Stop is the only consumer, and the mailbox stops after the drain, so the restored attempt could never be used. "No row" is the wrong criterion anyway, because reserved IDs go out before 31.
- The reviewer's R2 repro relied on a fan handback at suspend. Its keyboard-only form (a lock at the baseline read or at the readback) now restores Normal.

## Watch out

- The fixture read job sends no 00/84 query, so tests resolve uncertainty with `record(&rig, mode)` or `reader.record_driver_mode(..., at)`. The production observeState read job records one at the 15 s cadence.
- `fan_rows(&r)` is the journal helper in readiness_tests/mutation.rs; there is no `files.lines`.
- Counts in the test rig: a keyboard release uses 20 cleanup authorizations and 3 exchanges; the fan recipe used 60 more when it ran at suspend. The per-authorization cost on RZ09-0581 is not measured.
