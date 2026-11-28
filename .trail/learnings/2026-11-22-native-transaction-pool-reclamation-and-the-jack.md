---
title: Native transaction pool reclamation and the frozen-clock TFC test
author: Jack
date: 2026-11-22
branch: fix/native-transaction-pool-reclaim
---

## Decided
- The native transaction pool (IDs 1..=254, round-robin, `performance/transactions.rs`) frees an ID only when no reply bearing it can be read again. An unsent ID returns at Reader retirement, after `poll_retirement` confirms. A sent ID returns only when a later request with the same class and command is answered, because that reply replaced it whether the firmware keeps one reply buffer or one per command. A sent ID with no correlated terminal reply (busy, timeout, cancel, mismatch) is quarantined for the process. IDs that a fan record or a temporary-charge cancellation names stay pinned.
- `Attempt::transfer` is the only place that marks an ID sent or answered. `terminal_reply` rebuilds the sent request to correlate the reply, which is why the encoder guard allowlists `transactions.rs`.
- Pool exhaustion is named once, in `apply_preset`: `Reader.exhausted` plus `Refusal::TransactionCapacity`, even when the baseline read is the first reservation to fail.
- The Driver-mode packaging refusal stays. The pool no longer blocks it, but admitting it is a separate decision.

## Tried and ruled out
- Treating a closed and reopened handle as proof that a reply is gone. The GET_FEATURE mailbox outlives CancelIoEx and the handle, so native stays stricter than managed, which restarts IDs at 1 per reopened session.
- Making the near-capacity TFC test deterministic with fixture latency alone. `run_job` enforces the recipe deadline with `timeout_at` on the test's runtime (4 s for a source cancellation), and a 1 MiB journal costs about 630 ms there in debug. The fix is the `frozen` helper in readiness_tests/mutation.rs, which pauses the clock and moves it only between counted job or status-observer threads.
- Advancing a frozen clock only on thread exit. The thread-local exit counter fires a few instructions before `JoinHandle::is_finished`, so the Service's 5 ms poll needs one more step. Unlimited idle advances bring load dependence back through spawn gaps. Each exit now buys at most 4 polls.

## Watch out
- A prepared manual fan record pins about 24 IDs and a TFC enable pins 8 cancel IDs, for the process lifetime. Releasing them needs owner-lifetime hooks in fan_mutation.rs and battery_mutation.rs.
- `FanPermit::refused` ignores `TransactionCapacity`, so fan requests do not show the capacity text. Read jobs publish no reading on exhaustion.
- In a mutant run, a while-reserve loop never ends when Sent IDs are released (`quarantined_ids_exhaust_the_pool`). Run mutants under `timeout`.
- A stall inside a fixture `before_write` hook runs while the fixture State mutex is held, so it does not model a slow host. Stall `MemoryFiles::replace` instead.
