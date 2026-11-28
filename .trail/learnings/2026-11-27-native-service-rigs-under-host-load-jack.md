---
title: "Native Service rigs under host load: held clocks and job clocks"
author: Jack
date: 2026-11-27
branch: fix/native-realtime-rigs
---

## Decided
- Rig Stop/drain, origin ticks and source mutations run as held exchanges (Rig::drain, Rig::process_power_source, Rig::held). The driver_mode_owner shutdown helper uses Rig::drain too.
- HeldClocks::pace bills a descheduled gap in steps of at most 5 ms with the Service polled between them; the unbilled rest is kept as debt and billed by later steps. Only busy job-thread time goes unbilled. A test-chosen wait (hooks, Rig::parks) is billed in one step.
- Under cfg(test) only, performance Worker::start checks the spawn deadline on the Service runtime's clock, and mutation_fixture rebases a non-paused job's deadline by what the Service clock leaves. Production code path is unchanged.
- An owned-Stop stall on a paused job clock advances that clock (advance_paused_clock) instead of sleeping.
## Tried and ruled out
- Capping a pace step and dropping the rest of the gap: the held clock falls behind, CatchUp then over-ages readings taken in the exchange, and battery power goes stale before a test snapshot (thermal_presets, edge_stored_temporary_publication show the default slot).
- Capping steps during a test-chosen wait: it races real-time slack the test set.
## Watch out
- keep_real_clock tests (request_triggers a_step_dropped_at_its_bound, gpu_clock_tuning a_gpu_submission_outlasting), the 700-800 ms window in a_stalled_release_job_ends_at_its_own_budget, gpu_clock_offsets and mailbox_read real-time tests still fail on a heavily loaded host, at base too.
- Many tests still call source.drain() directly on the running clock.
