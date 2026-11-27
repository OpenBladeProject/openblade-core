---
title: "Native lighting stream lease reopen, shared frame admission and drain plan"
author: Jack
date: 2026-11-27
branch: feat/native-lighting-controls
---

## Decided
- A stream frame marks the session's stream open before it reaches the port (`lighting.stream_open.set(true)` in `Runtime::stream_frame`). The matrix lane reopens the collection for a frame when it holds none, as after an open that timed out while the previous renderer kept streaming, so the next retire must close it. Stop's settle retires the port unconditionally for the same reason.
- Frame admission order lives in one function, `thermal_native::admit_frame` (epoch, session, power, epoch, then the custody's frame part), and the custody's frame part in `mailbox_read::authorize_original_frame`. `WindowsGuard`, `ReadLease` and the test fixtures (`Custody::admit_frame`, `PinnedFresh`) all call them, so a fixture cannot pass while production's order differs.
- The Stop drain shares are a build-time plan in `readiness.rs` (`drain_plan`): stream settle 300 ms, display brightness 300 ms, keyboard 900 ms and a 1.4 s fan minimum must fit `SOURCE_DRAIN_TIMEOUT`, with and without the keyboard share. Each feature's real budget is asserted against its share.

## Tried and ruled out
- `scoped_fresh` for frame tests: its `CapturedFresh` uses the default `Fresh::authorize_frame` (None), so every frame refuses and a refusal test passes vacuously. Use `pinned_fresh`.
- `pinned_fresh` before `PinnedFresh` delegated `power_generation`: a lighting request then returns status 2, because the lighting mutation needs a power generation.

## Watch out
- The open_matrix `MATRIX_IO` refusal is untested: `NativePort` needs a live control lease and the fixture port has no `MATRIX_IO`.
- A render job can refuse for up to one 100 ms poll after a long-outstanding report completes; this is documented, not fixed.
