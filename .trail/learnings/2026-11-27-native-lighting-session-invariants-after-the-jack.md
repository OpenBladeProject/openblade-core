---
title: Native lighting session invariants after the L2 review rounds
author: Jack
date: 2026-11-27
branch: feat/native-lighting-matrix
---

## Decided
- This replaces two points in the earlier L2 learning. The keepalive grid starts when the drawing request completes, not at the first frame. Runtime cadence tests no longer pause and advance the clock: each tick gets an explicit instant, taken from `Runtime::keepalive_due_for_tests`, so a slow host moves no frame.
- A job that may submit holds a `Claim` on `lighting_mutation::Session` until its outcome is read. Dropping the claim unsettled latches the doubt, for example when the request or tick is cancelled while the worker still runs the job.
- `Session::latch` always ends the renderer. Nothing owns the matrix while the latch is set, and the request after the read-only resolution draws anew.
- `Runtime::maintain_lighting` takes `gate.try_lock()` before it reads or changes the session. If a request holds the gate, it returns at once and starts no job. Stop's `retire_renderer` is the only change made without the gate.
- `Owner` records the low-power generation it was drawn under (`Runtime::lighting_boundary`, the lifecycle `Transition::generation`). `crossed_low_power` is true when the gate reads low, or when that generation has moved. Both the tick and the next request check it, so a suspend and resume that both land while requests hold the gate still end the renderer.
- The next keepalive instant is computed from the tick's instant plus the step's own elapsed time, which matches managed's observedAfterWrite.

## Tried and ruled out
- Retiring the renderer in `prepare_for_low_power`. That path exists only with `service-keyboard-driver-mode-v1`, so profile-triggers-only builds would keep the bug.
- The pre-delta order, checking low power before the gate. It misses a boundary when no tick lands between suspend and resume; `a_request_after_a_low_power_boundary_draws_anew` catches it.

## Watch out
- The generic `RoleIdentity::select` binds the device to the host model's catalog entry. No selection can differ from the reviewed identity in model alone or device alone, so `reviewed_matrix`'s clauses are tested on an identity with one field changed.
- The fixture's lighting jobs run on the held test clock, which does not advance while a job thread is busy. A test cannot make a keepalive job take test-clock time, so the post-job advance has no direct test.
- `with_tick_during` polls the tick once and asserts that it is ready. That catches a tick acting under a held gate without waiting for a harness timeout.
