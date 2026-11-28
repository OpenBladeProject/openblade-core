---
title: Native Driver-mode owner resolves an Indeterminate acquire read-only
author: Jack
date: 2026-11-24
branch: fix/native-driver-mode-indeterminate
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/driver_mode_owner.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/driver_mode_owner.rs
  - docs/windows-rust-development-service.md
---

This amends `2026-11-24-native-low-power-driver-mode-release-jack.md` and the 6a-3 learnings on one point: an Indeterminate acquire no longer stops the owner for the process. Their other decisions stand.

## Decided

- Each later due step resolves an Indeterminate acquire read-only (`Runtime::resolve_indeterminate`). It takes a fresh console capture, keeps the acquire's journal and fan skip conditions, and runs one `DriverModePermit::observe` job: `DeviceModeJob::new(Driver, None, ..)`, whose `write_from: None` reads 00/84 and never reaches the claim or the setter.
- Observed(Driver) keeps `owns` as it is (the acquire's own pre-submission claim, or false after a release ended it) and publishes true. Observed(Normal) clears `owns`, publishes false and sets `due` to None, so the next tick runs the ordinary acquire: fresh capture, read-before-write, new claim, setter on a new ID. Unread publishes null and stays Indeterminate; Pending records nothing. The recovery latch resets on either confirmed mode.
- The resolving tick sends at most one 00/84 and no 00/04. Managed has no Indeterminate state; its one port recovery may read Normal and write Driver in the same step. That deviation is documented in the module doc and in docs/windows-rust-development-service.md.
- The resolution runs after the verified-device check and the release-uncertain block, so an unverified device or an unresolved release doubt defers it.
- `run_console_job` is shared by the acquire and the resolution: mutate under `INTERVAL`, only fatal admission evidence is an error.

## Tried and ruled out

- Resolving from the read cadence (`driver_mode_confirmed_since`), as the release-uncertain case does. The brief asked for a read under fresh admission, and the owner's own job is the only one the epoch tests can fence. A read-cadence shortcut could be added later.
- A notice "between" the post-settle and post-decode checkpoints. The rig's `ReadAdmission` carries the lifecycle epoch, so a notice delivered inside any authorization fails that authorization itself. The notice test walks the send and all four authorizations after it; dropping either query checkpoint shifts where the notice lands, so the "notice was delivered" assertion kills M4a and M4b.

## Watch out

- Hand mutants, all killed: skip the resolution, resolve with the acquire permit, Unread or Pending clears Indeterminate, write in the same tick after resolving, Normal keeps the claim, Driver claims, Normal not due, drop either or both query checkpoints, drop the skip conditions. The harness is in the private scratch folder, not the repo.
- `fenced_readback_rig` sets `pinned_fresh`. Turn it off before a request that needs `power_generation` (see `an_unsettled_journal_row_defers_the_resolution`).
- Tests that assumed the owner stops forever now expect a resolution read at the next `later()`. Count one extra 00/84 per resolving step.
- Unchanged and still blocking packaging: durable owned-or-uncertain record (6a-5), attended RZ09-0581 timing and physical validation. Nothing here touched hardware.
