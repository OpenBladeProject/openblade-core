---
title: "Native brightness boundary release is owed, not gated"
author: Jack
date: 2026-11-26
branch: feat/native-brightness-hid-usage
---

## Decided
- At Stop or a confirmed suspend, `release_display_brightness` first `notify_waiters()` on the brightness `boundary` Notify, which ends every brightness request still waiting (status 3, INTERRUPTED), then queues the release without the operation gate. The lane already orders it behind a press still running; the gate only starved the keyboard's Normal release (it takes the same gate) and made the release give up unqueued.
- An unanswered boundary release is tracked by a lane ticket (`Held::owed`, `Lane::owed_end`): `asserted` reads Released while it is queued, no second owed release is queued, and `reconcile` settles its late answer (Applied clears the held state). `release_owed` takes an `owes` predicate and reconciles before deciding.
- The lane keeps one extra channel place for `Waiting::Stays`, counted by `queued`, so withdrawn calls still in the channel never refuse the owed release. GPU behaviour is unchanged.
- The neutralization has its own 15 s due (`neutralize_due`), because the maintenance step returns before its own `due` when the record is closed or a release is in doubt.
- The driver-trust cache is never locked across WinVerifyTrust; the transport's SERIAL mutex is gone.

## Tried and ruled out
- Sharing the lifecycle suspend Notify: it is `notify_one` with a single permit consumed by the origin tick loop.
- Clearing `release_required` when an owed release is merely queued: the IOCTL can still fail.

## Watch out
- The earlier learning that the boundary release "checks release_required before waiting on the gate, then re-checks under it" no longer holds; there is no gate wait.
- An interrupted press that had started stays held; its release follows on the lane. Tests that join a press with a suspend tick must use `join!`, not a `select!` that expects the press to outlast the tick.
