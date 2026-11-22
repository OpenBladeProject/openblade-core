---
title: Native battery cancel defers when a later notice fences its tick
author: Jack
date: 2026-11-22
branch: fix/native-tfc-cancel-epoch-race
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/battery_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/temporary_charge_release.rs
  - docs/windows-native-thermal-mutation.md
---

## Decided
- `battery_source_event` peeks at the temporary-charge owner and runs its one capture and one `authorize` (`admit_source_cancel`) before it claims anything. A proof whose lifecycle differs from the tick's epoch (`SourceCancel::Superseded`) means a fencing notice (a second PBT_APMPOWERSTATUSCHANGE, a resume, a session change) landed after the status tick read its epoch: return `Ok(None)` with the owner untaken, unabandoned and nothing submitted. The newer epoch makes `temporary_event_needed` true on the next tick, which reads the source again and writes the cancel.
- A refused capture, a refused authorization (`None`), or a generation, storage, custody or authentication mismatch still claims once and pauses (`SourceCancel::Refused`), as before. A claimed or possibly submitted cancel is never retried: the claim happens before `reader.mutate`, and an abandoned owner never reaches the admission because `reconcile_temporary_owner` releases it or returns the deferral first.
- Retries need no time bound. Each deferral needs a strictly newer epoch, and only an SCM notice advances it, so retries never outnumber notices. Managed's nearest analogue is `ContinueDeferredTransitionAsync` (ServiceControlRuntime.cs:17668-17730): a step proven never submitted is retried on later periodic observations while the power source is unchanged, with a two-minute continuation expiry. Native needs no expiry because a deferral holds no write authority and the next tick recaptures everything.
- The Battery evidence publication already clears the in-memory TFC intent (`desired_battery(1)` drops `battery_temporary`), so a deferral does not leave desired TFC visible on Battery; the owner's own record still carries the cancel.
- `prepared_race` (tfc_release_actual_worker_prepared_race_* and tfc_release_outcome_cause_changes_*) was flaky because its padded 1 MiB journal costs about 0.8 s of debug CPU per request against the pipe's 6 s `RECIPE_BUDGET`; under load `serve_mutation`'s `timeout_at` dropped the connection (UnexpectedEof at `outer_request`) or the late result reported "lost original caller/owner admission". It now sends its mutations straight to the source with a 120 s hang-guard deadline, drives the release ticks itself with `origin_tick`, and uses the same guard for the worker hooks and the outer timeout.

## Tried and ruled out
- A paused tokio clock for `prepared_race`. The Reader worker is an owned OS thread, so the paused runtime auto-advances while it waits on the worker: the 500 ms origin interval and then the request deadline fire in virtual time before the worker finishes.
- Considered, not built: taking the owner before the capture and putting it back on supersession. It adds a take/restore pair on the path the 2026-11-14 learning warns about; peeking leaves the owner untouched until the claim.

## Watch out
- A notice that lands after the single authorize but before the worker's own per-write admission still fails that admission after the claim, so the cancel pauses as before. Closing that window needs un-claiming after a known-unsubmitted worker refusal, which touches the worker and journal and was not attempted.
- A suspend delivered inside the authorize makes it return `None` (the gate refuses), so it pauses rather than defers.
- The `admit_source_cancel` record check (`record.filter(desired.temporary)`) is unreachable for a confirmed owner; its mutant survives as equivalent.
- `debug_assert!` at the claim checks the taken owner is the peeked one. A mutant that takes on peek trips it, which is part of that mutant's kill evidence.
