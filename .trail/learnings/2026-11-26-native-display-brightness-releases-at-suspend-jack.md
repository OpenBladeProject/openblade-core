---
title: "Native display-brightness releases at suspend, maintenance and Stop"
author: Jack
date: 2026-11-26
branch: feat/native-brightness-hid-usage
touches:
  - ".github/workflows/rust.yml"
  - docs/windows-rust-development-service.md
  - tools/openblade-windows-host/src/diagnostic_export/history.rs
  - tools/openblade-windows-host/src/diagnostic_export/tests.rs
  - tools/openblade-windows-host/src/driver_trust.rs
  - tools/openblade-windows-host/src/driver_trust/paths.rs
  - tools/openblade-windows-host/src/driver_trust/tests.rs
  - tools/openblade-windows-host/src/driver_trust/windows.rs
  - tools/openblade-windows-host/src/installed_main.rs
  - tools/openblade-windows-host/src/service_status.rs
---

## Decided
- Supersedes the earlier "no low-power release or maintenance neutralization" note: with `service-keyboard-driver-mode-v1`, a confirmed suspend releases a held brightness usage before the keyboard (`low_power_cleanup.rs`), and the maintenance step neutralizes once the device is verified (`driver_mode_owner.rs`).
- Cleanup releases (Stop, suspend, maintenance) use `hid::release_owed`: no 250 ms answer margin, and `Waiting::Stays`, so a release queued behind a press whose request is gone runs after it instead of being withdrawn. Request releases keep the margin and withdraw.
- `release_display_brightness` checks `release_required` before waiting on the gate, then re-checks under it. Stop's 300 ms share comes out of the fans' part of the drain; the suspend share is only what the window holds beyond the keyboard's 900 ms.
- The GPU clock-offset worker and the brightness lane share `withdrawal_lane::Lane` (Claim QUEUED/STARTED/WITHDRAWN, Recover on panic). Availability probes run on a separate thread so a release never waits behind one.
- A press takes its own fresh capture (`Brightness::guard`) without a profile store; the shared factory refuses non-`S-1-5-21-` SIDs and failed store opens.
- `serve_mutation` skips the pre-mutation status snapshot for this request; a repeat or release reaches no device work.

## Tried and ruled out
- Publishing availability from a probe per status: it would block the status observer on a Windows call, so availability lags one status by design.

## Watch out
- `driver_mode_tests::the_compiled_identity_ends_with_the_refused_marker` strips markers in build.rs append order (brightness, hyperboost, exit, export, live); a new marker needs its place there.
- `Log::new` and `Lane::submit` are cfg-gated (export / test); the production log without the export is `Arc::default()`.
