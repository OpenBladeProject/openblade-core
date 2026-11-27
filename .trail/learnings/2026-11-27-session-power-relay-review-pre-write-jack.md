---
title: "Session power relay review: pre-write gate, callback context, decode parity"
author: Jack
date: 2026-11-27
branch: feat/native-agent-power-automation
---

## Decided
- The session power relay checks each report once more immediately before its first byte is written: `ReportChannel::exchange_on` takes a `before_write` hook that runs after the pipe peer verifies, refuses the write or returns the reply budget. The relay's `Gate::admit` re-reads QPC and the cancellation there; the application reporter passes `|| Ok(REPORT_REPLY_BUDGET)`. A budget read when the report starts is stale by the write, because the origin check, the channel's Service admission and the peer check can re-hash the installed package.
- A gate refusal is an `io::Error` carrying the private `Abandoned` type, so `serve` records it as Abandoned, not Failed.
- The relay registration resolves PowerRegister/UnregisterSuspendResumeNotification from System32 powrprof at run time through `session_native/powrprof.rs`, shared with the power-mode supplier. The candidate's import table must not name powrprof.dll (check with a PE import read, for example the review's imports.pl).
- The callback context counts callbacks inside it; Drop releases it only at zero after a successful unregistration, waiting at most BUDGET + 500 ms, else it leaks it and records `CallbackRetained`.
- Power-mode status decode: an absent `unconfirmedWrites` is empty and null is refused, as managed (`ServiceMessages.cs` default, `IpcMessageCodec` null check). Member casing differs: native reads camelCase only, managed binds case-insensitively; documented, not matched.
- The one `stopped(&mut watch::Receiver<bool>)` lives in `feature_lifetime.rs`; every Agent role module is under `openblade_agent_endpoint`.

## Tried and ruled out
- Pinning budget expiry with a test that later sends stop: stop emits the same Abandoned notice, so mutants removing the send bound or expire's cancel survived. Assert the notice before stop, on the paused clock, one millisecond either side of the budget.
- Awaiting `serve` without a stop in an origin-failure test: a mutant that skips the check hangs until the CI timeout instead of failing. Wrap it in `tokio::time::timeout`, which auto-advances on the paused clock.
- Expressing absent versus null `unconfirmedWrites` in the managed power-mode vector recorder: it replays typed `ServiceStatusSnapshot` objects, so the codec distinction cannot be recorded there; the managed codec tests pin it.

## Watch out
- A callback Windows dispatched that has not yet incremented the in-flight count is not counted; the design relies on unregistration waiting for those.
- Two statuses arriving before the power-mode runner runs can end differently from managed's inline apply; the vectors observe each status with the runner idle and do not cover it.
