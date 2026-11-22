---
title: "Native request-trigger round 2: bounded debts, static refusals and the step bound"
author: Jack
date: 2026-11-22
branch: feat/native-profile-request-triggers
---

## Decided
- Native request triggers bound each owed reconciliation (`triggers::Debt` in request_triggers.rs). A pair owes one at every class from Service start and after each change. Every reconciliation whose steps ran settles it. The one exception is a transient refusal (`Stepped::transient`: pace, an unclassified adapter, a failed store read, or time or admission that ran out before the intent row) where nothing was possibly submitted and the store is not fenced. That debt is retried on at most `RETRIES` = 2 later reports. A renewal arrives every 5 s, so an unbounded debt read firmware and drew transaction pairs on each one.
- Refusals that saved intent alone decides (GPU clock tuning, no eligible destination, ManualCurve, manual fans on battery) run in `refused_by_saved_intent` before any firmware read or pair. They apply only when the trigger's classes equal `Classes::admitted(fresh)`, because otherwise the reading could find an unchanged class, and that reply is different.
- A save keeps any earlier debt unless a step's reading found one of that debt's classes (`Reconciled::read`). Finding an unchanged class leaves the prior debt as it was.
- A reconciliation step is dropped at `deadline + RECONCILE_MARGIN` (1 s, under FRAME_TIMEOUT so the Agent's 13 s wait covers it). This uses the explicit path's drop safety: the Reader keeps the worker, the next mutation drains it, and an unsettled journal row fences the store. The reply is status 3, even after a save.
- A fan step refused for pace after a confirmed preset replies status 3. That is managed parity: `AnyConfirmedWrites` maps to Failed in the ToOperationResult vectors.
- Performance provenance source 2 for overrides that set only Custom levels or only GPU tuning is an intentional ungated parity fix. It is backed by the profile-resolution-vectors.json case "an override of only custom levels or only gpu tuning is an application source".

## Tried and ruled out
- Treating `Cause::CurrentAdmission` as non-transient after acceptance. At a device pace of 900 ms the job limit lapses before acceptance, the startup debt was settled, and the rule never applied.
- Settling a prior debt on any save that ran steps. A save at another class then swallowed the debt of a report made on unknown power.

## Watch out
- `apply_fan_locked` calls `reader.drain_active()` first, so a fan request after a hung reconcile waits on the worker. A performance request is refused by the pending fence before it drains. Held-transfer tests should use performance requests.
- clippy `await_holding_lock` does not see `drop(guard)`. Read fixture state in a scoped block.
- The wait at the mutation gate itself is not bounded by RECONCILE_MARGIN.
