---
title: "Native recipe pacing: reply-to-reply exchanges, a strict deadline, paused Rig jobs"
author: Jack
date: 2026-11-21
branch: fix/native-recipe-pace-hardening
touches:
  - tools/openblade-windows-host/src/discovery/hid_lease/performance.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/fan.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation_fixture.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation_pace_tests.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/worker_tests.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
---

This supersedes the exchange timing and the comparison in 2026-11-21-native-recipe-pacing-two-longest-exchanges-jack.md. Where the check runs, the formula's terms and the refusal handling are unchanged.

## Decided
- `Exchanges` (performance.rs) times each exchange from the previous reply, the first from `Pace::start`. So every interval includes the work before its send: discovery, authorization, and each checkpoint's two authorizations, custody check and origin verification.
- `Pace::prepared` moves the mark past the Prepared append, because `covers` already charges that append for every row left.
- `covers` refuses unless `now + slowest + (E-1)*second + A*append + fixed < deadline`. The comparison is strict because `Limit::check` fails at `now >= deadline`, so a projection ending exactly at the deadline used to start and then overrun.
- The projection still excludes retirement after the closing row. It charges every remaining exchange at a measured one, so per-exchange work that grows after the check is not charged.
- The Fixture's worker (mutation_fixture.rs) runs observations, thermal and fan recipes, and any job with a latency on paused time unless a hold (`held_read`, `held_write`) is set. Rig requests record `arrival`, and every paused recipe, battery and temporary-charge ones with a latency included, takes its deadline as the request budget from its own start. Host load therefore changes neither the measured pace nor the remaining budget. Without that, `temporary_charge_runs_within_its_request_budget_at_slow_transfers` spent about 5.7 s of paused time at 400 ms transfers against a 6 s deadline taken earlier on the real clock.
- A test that needs a deadline to pass while a worker is parked waits for an event, then calls `tokio::time::pause()` and `advance` to the deadline, then `resume()` before it waits on the worker's OS thread again. The deadline is long enough (a 60 s read budget in worker_tests.rs, the 6 s recipe cap in `correction_late_accepted_intent_reconciles_only_with_new_read_without_replay`) that the job reaches the park on any host. The late-success worker test and the late-intent correction test used to need a 500 ms and a 1 s real deadline to be beaten.
- `RetirementGate` (readiness_tests/mutation.rs) parks the worker at mutation retirement on a Condvar and announces it with a Notify, replacing flag spins in the late-intent, edge-canceled and disconnected tests. The fixture's `holding` Notify fires when a held read or write begins, replacing the `writes != 1` spin in `battery_correction_queued_result_preserves_latest_thermal_metadata_and_witness`. `Rig::request_as` now reports the server's error before reading a missing reply.
- Battery and temporary-charge recipes without a latency keep the real clock. The tests age their results on the test's clock, and a paused job's waits run ahead of it: `battery_publication_policy_confirmation_does_not_renew_temporary_age` and `tfc_correction_same_sid_current_session_cancel_has_new_identity_and_proof` failed when those recipes were paused.
- Pausing only thermal and fan recipes: under 56 CPU burners, 7 fan and thermal tests still failed with a lost result or an unimplemented recipe while observations ran on the real clock.

## Tried and ruled out
- Timing exchanges from send to reply: Silent passed the check and overran at 210-320 ms per checkpoint, and Custom at 126-230 ms, leaving setters submitted.
- Pausing the whole Rig test for a late deadline (ruled out by reading the code, not run): once paused, every wait on a worker's OS thread auto-advances through `Worker::wait`'s POLL sleeps, so a 2 s liveness bound passes in virtual time before the thread finishes. Pause only around the `advance`.
- Making the Rig tests `start_paused` (rejected by reading the code, not run): the worker is an OS thread, and the outer `Worker::wait` loop would auto-advance the paused clock to the deadline while the thread works.

## Watch out
- Paced thresholds at 5 ms transfers, with a checkpoint of C ms (two authorizations of C/2):
  - Silent runs at 200 and is refused at 210.
  - Custom from Balanced: 116/120. Custom from Custom: 100/110.
  - Fixed fan entry: 140/150.
- Thresholds at 230 ms transfers:
  - Prepared append: Silent 654/655, Custom 254/255.
  - Single 07/8C stall: 1960/1965.
  - Fixed entry from Performance is refused at 3725 and runs at 3730.
  - Return to a saved Custom: 3450/3455.
- A store stall made with a real `thread::sleep` is invisible to a paused job. Use `MemoryFiles::paced_replace` for a slow journal, and `power_latency` or `setter_latency` for device stalls, instead of a sleeping hook.
- `exchange_stall` (mutation_fixture.rs) adds latency to the Nth sent request of any class. `the_first_exchange_and_an_earlier_slowest_are_both_charged` uses it to pin that the first exchange is timed from `Pace::start` and that a new slowest demotes the old one to second.
- Other `on_mutation_retired` hooks in readiness_tests/mutation.rs still spin on a flag (the prepared-settlement, timestamp, terminal-failure and submitted/unsubmitted tests). A spinning test also takes CPU from its neighbours: with the listed spins gone, the TFC release pair and `tfc_v1_near_capacity_performance_refuses_before_spending_reserved_source_cancel` stopped failing under burners, though that is an observation, not a proof.
- The TFC release tests drive the real service loop, its 500 ms origin tick and battery jobs on the real clock. Under extreme CPU starvation their CPU work alone can exceed the production 6 s request deadline ("lost original caller/owner admission"). No clock change in the test fixes that without injecting a clock into production code.
- Paused jobs do not make the Rig fully load-proof. The outer test runtime stays on the wall clock: observation freshness, the 5-20 ms select and timeout guards, and the Stop drain. With 56 busy loops on 16 cores, the old fixture failed 5 and 22 of the 172 mutation tests, and the final one failed 0 and 14. With 16 busy loops, or with two lane builds, both passed.
- After the event and paused-deadline changes, the 11 tests a review found wall-clock fragile, run together under 56 busy loops, passed 10 of 10 rounds, against 0 of 3 before (5 to 9 failures a round). The whole profiles lane under 56 busy loops failed 3 of 3 runs before, each on one of those 11, and 2 of 6 after; those failures are other tests' real-clock windows (one battery exhausted-read test, two edge tests, one 0581 correction and one fan handback), not those 11. Two continuous host builds never reproduced a failure on either side: the five lanes passed 20 of 20 runs at the head (6-14 s a lane) and at the base (30-50 s a lane).
