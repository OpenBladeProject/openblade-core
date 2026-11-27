---
title: "Native lighting stream review: matrix lane, bounded waits, admission split"
author: Jack
date: 2026-11-27
branch: feat/native-lighting-controls
touches:
  - docs/windows-native-lighting.md
  - tools/openblade-windows-host/src/discovery/hid_lease/performance.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/lighting.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/lighting/render.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/lighting_effects/tests.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/lighting_guard_tests.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation_fixture.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/windows/stream.rs
  - tools/openblade-windows-host/src/windows/scm/installed/lighting_controls.rs
  - tools/openblade-windows-host/src/windows/scm/installed/lighting_mutation.rs
---

## Decided
- The native lighting stream's Windows writer sits behind `MatrixIo` (`performance/matrix_lane.rs`), with the Win32 calls in `performance/windows/stream.rs`. `Lane::frame` checks the recall epoch, the descriptor, the deadline and the `MATRIX_IO` CAS, then the epoch again just before SET_FEATURE. A report holds `MATRIX_IO` and the lease until its terminal completion. The lane thread polls an outstanding report between requests, so a late completion releases both even after a timeout or retire.
- `MatrixStream` is `open`/`frame(report, deadline)`/`retire`/`settle(deadline)`. Every runtime wait on it is bounded with `timeout_at`. A frame past its 500 ms budget counts as in doubt (latch, retire, gate released). Stop runs `retire_renderer` and then `settle_lighting_stream(drain deadline)`, which latches if no settle is confirmed in time.
- `stream_frame` returns `Next::At(instant)` or `Next::Wake`. A stale reading returns `Wake`, and the stream loop waits for the tick's `wake.notify_one()`.
- The origin tick waits for the gate when a stream frame holds it (`stream_hold`, bounded by that frame's deadline), so in-flight frames do not cost the tick its clean reading. A request holding the gate is still skipped.
- The split is deliberate: each frame checks `WindowsGuard::authorize_frame` (epoch, console session unlocked, AC line status known, epoch again, custody). The tick's reading (≤1 s) covers origin and package verification, SCM, primary SID and the controller inventory. `confirm_stream_clean` uses `Fresh::admitted_at_capture()`, which is true for WindowsGuard because `capture` already ran `admit_for(false)`. This corrects the L3 learning's claim that WindowsGuard refuses only in `authorize`. The fake still needs its `authorize()` pass.
- An animated `setLightingOverride` opens the stream lease inside the request (`Work::Unopened` stores the intent and replies collection unavailable). The lease stays open while one animated renderer replaces another.
- A step's wake-from-Off baseline reads the base power profile (`profile_layer(&Foreground::default())`), not the application layer. On an unclassified adapter, a refresh change needs a lighting admission (`Intent::Admit`).

## Tried and ruled out
- Bounding the refresh-retime test by the wall clock alone: the fixture runs jobs on a paused runtime whose clock is ahead, so the handshake `Instant` was about 200 ms in the future. Render now clamps the stream start to `min(now)`.

## Watch out
- Run long cargo builds with nohup plus a `.done` file, then wait in separate calls of about 160 s or less. `timeout 175` kills cargo with exit 143.
- `lighting_guard_tests.rs` pins every `LightingJob` constructor and user file. Add new ones (`unopened(`) there.
