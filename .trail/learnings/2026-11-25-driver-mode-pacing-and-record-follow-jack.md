---
title: Driver-mode pacing and record follow-ups after the delta review
author: Jack
date: 2026-11-25
branch: feat/native-suspend-hold
---

## Decided
- The post-claim pace recheck is pinned on paused time in `mutation_pace_tests.rs` (`a_driver_mode_setter_is_sent_only_while_it_fits_after_the_claim`): a 200 ms claim sends its setter in a 900 ms budget and a 220 ms claim does not. The 200 ms case kills a recheck that recomputes `setter_fixed` after the claim (charging it twice); the 220 ms case kills dropping the recheck. The rig test `a_release_claim_slower_than_its_allowance_sends_no_setter` keeps one real-clock case whose margins only grow under load.
- An `AcquireIntent` that a release reads Normal settles `Clear` and reports `NotDriver`; only `Owned` read Normal is a `ForeignChange`. A suspend between an acquire claim and its setter is the path: the suspend refuses the setter and the ordinary admission of the revert, so the intent stays.
- The store admits a replace by `history::follows` and CAS alone; the ID rules (settle keeps the intent's IDs, revert restores the replaced record's) are the ledger's. `every_transition_vector_is_admitted_exactly_as_it_follows` replays every JSON vector through the real Store, so a loosened domain edge fails a host test too.
- The rig's `Custody::read_refuse_at` refuses the nth ordinary read authorization once. In the maintain path's own-release doubt branch, #1 is `driver_mode_confirmed_since` and #2 is the ledger settle's admission check; arming 2 refuses only the settle (a settle that skips its admission check makes the test fail on the write count).

## Tried and ruled out
- Chasing the other real-clock release tests that fail with one busy loop per core (`slow_admissions_leave_the_release_job_only_the_rest_of_the_share`, `a_stalled_release_job_ends_at_its_own_budget`, `a_stop_during_the_low_power_release_reads_the_dropped_job`, `a_dropped_low_power_release_leaves_its_intent_for_a_read`, `a_release_without_time_for_the_setter_is_exhausted`, `a_stalled_keyboard_release_leaves_the_fans_their_drain_time`, `the_claim_precedes_submission_and_a_failed_submission_is_indeterminate`): all predate the pacing round and pass unloaded. They were left out of scope.

## Watch out
- `Standing::owns` is test-only now. A new release outcome that needs it must re-check what an `AcquireIntent` read Normal means before reusing it.
- With all cores saturated, those real-clock release tests fail in 2 to 6 of 753 per run. A failure among them under load is not a regression signal by itself. Rerun unloaded first.
