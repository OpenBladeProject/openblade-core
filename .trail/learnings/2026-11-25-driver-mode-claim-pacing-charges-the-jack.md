---
title: "Driver-mode claim pacing charges the floor, then rechecks"
author: Jack
date: 2026-11-25
branch: feat/native-suspend-hold
touches:
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/device_mode.rs
  - tools/openblade-windows-host/src/windows/scm/installed/driver_mode_ledger.rs
  - tools/openblade-windows-host/src/windows/scm/installed/driver_mode_owner.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/driver_mode_record_tests.rs
  - docs/windows-rust-development-service.md
---

## Decided

- A Driver-mode job charges its claim's record writes at `REPLACE_FLOOR` (25 ms) each before the claim (`Ledger::claim_allowance`; two writes for a release over an AcquireIntent), never at the last measured replace. A refused job writes nothing and so measures nothing, so one slow sample charged before the claim refused every later release and acquire for the rest of the process.
- After `job.record(DeviceModeStep::Claimed)` the job checks `Pace::covers` again with the pre-claim `fixed` and the real elapsed time, charging the setter, its acknowledgement and the readback (2 exchanges, 0 setters). A claim that took too long sends no setter; the step stays `Claimed` and the owner reverts the intent.
- A release whose step is `Claimed` now reports `Exhausted` (share spent or admission still held) or `Unadmitted`, after the revert. It used to report `Unconfirmed`, which claimed a doubt that a known-unsent setter cannot create.
- The settle after a job still charges `write_allowance` = max(floor, last measured), which the job's own claim has just refreshed, so the settle is charged at its claim's real length.

## Tried and ruled out

- Charging `write_allowance() * writes` before the claim (the original code). `a_slow_record_write_never_refuses_a_later_claim` and `one_slow_record_write_leaves_the_next_release_and_acquire_whole` kill that mutant.
- Relying on the pre-claim estimate alone. Dropping the post-claim recheck is killed by `a_release_claim_slower_than_its_allowance_sends_no_setter`.

## Watch out

- When the revert itself does not fit the release share (for example a 560 ms claim against 30 ms exchanges), the ReleaseIntent stays on disk as a doubt for a later read. "No doubt" holds only when the share has room for the revert.
- The 25 ms floor is an estimate and has not been timed on RZ09-0581 hardware. The slow-write tests use real wall-clock sleeps, so their margins depend on host load.
