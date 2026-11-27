---
title: Native lighting stream frame admission and bounds
author: Jack
date: 2026-11-27
branch: feat/native-lighting-controls
---

## Decided
- A native lighting stream frame runs no origin verification. `WindowsGuard::authorize_frame` checks the epoch, the WTS session, `GetSystemPowerStatus` and the epoch again, then calls `ReadCustody::authorize_frame`. On `ReadLease` that is the latched state, `Origin::verify_lifetime` (the revocation flag) and the active lifecycle. `GuardedReadCustody` adds only the retained failure.
- `Origin::verify` runs once per origin tick, inside the tick's full `capture` (`ReadCustody::authorize`). It covers the package ACL and `current.bin`, the SCM calls, `managed-handoff.bin` and the journal custody. This corrects the review-round learning, which said the frame skipped only the SCM query and the inventory: until this change the frame's `custody.authorize()` ran `Origin::verify` twice per frame.
- Stop's stream settle takes its own 300 ms share (`STREAM_SETTLE_BUDGET`, at most `FRAME_BUDGET`). An unconfirmed settle latches the doubt and the drain continues, so the fans and the keyboard are still released.
- A request's stream open has its own 1 s bound (`OPEN_BUDGET`). On timeout `close_stream` retires, which advances the lane epoch and sets the lane's cancel flag. The flag is passed to `select_matrix`, and the request takes `Work::Unopened`.
- `Lane::open` checks `describes()` when it reuses an open lease and the epoch again after an open. `MatrixIo::now()` is the lane's clock, so tests move it past a deadline instead of racing a real one.
- `NativePort::open_matrix` refuses while `MATRIX_IO` is held, so a render job refuses before any setter when the lane holds a report that never completes.

## Tried and ruled out
- Counting origin verifications on `WindowsGuard`: it has no supplier seam. The counts are on `GuardedReadCustody`, in a unit test with a counting original, and on the fixture custody's `full_authorizations`.
- Comparing the effect start with the first frame on paused job time: the job clock runs ahead. `lighting_real_clock` runs render jobs on the real clock.

## Watch out
- In `LaneStream::retire`, advance the epoch before setting the cancel flag. `Lane::open` clears the flag before it reads the epoch; that order is what prevents a missed cancel.
- `Session::retire` already runs `close_stream`. Stop must not retire the port a second time, and the Stop test expects one retire.
