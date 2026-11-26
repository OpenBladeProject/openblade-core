---
title: Native HyperBoost status members and recovery request (H3b)
author: Jack
date: 2026-11-26
branch: feat/native-hyperboost-status
---

## Decided
- `padlessHyperBoostEligibility`, `hyperBoostRestorePending`, `hyperBoostBlockingControllers` and `recoverHyperBoostRestore` are already in the managed contract at protocol 90. Native fills and serves them with no protocol bump: its `Snapshot` passes unknown members through `feature_fields`, and an absent member is managed's default.
- The three members are read by the metadata observer (`status_source::observe_hyperboost`), not by the per-user thermal projection: they are machine facts every caller sees, the observer has its own thread and budget for the HID scan, and the two per-user policy closures (`thermal_policy` and the fan one) would each need the projection.
- The pad and managed's journal are read only on the host matched as RZ09-0581 and only where the probes exist (lifecycle triggers). The blockers are the controller observation's own `hyperboost_blockers`, filtered to the status's potential controllers (`HyperBoostStatus::blocking`).
- `hyperBoostRestorePending` reports managed's journal state RestorePending and nothing native. `recoverHyperBoostRestore` reads that journal under the gate and answers; it writes nothing.
- A journal that cannot be read keeps the pending restore the last status published (`HyperBoostStatus::resolved`); with none before it, it is pending. An invalid document is not pending, as in managed.
- The source drain cancels the status observer first and waits for it last, to the drain's deadline only (`retire_observer`). The observer holds no device handle or gate, so the keyboard release, fan handback and HyperBoost exit no longer wait behind it. The pad scan takes the observer's cancel flag.
- Packaging of `service-hyperboost-v1` stays refused.

## Tried and ruled out
- Mapping an unsettled native exit row to `hyperBoostRestorePending`: the handback already classifies that row for managed as a pending write confirmation (`MatchNativeHandback`), not a restore, and the tray's text for the member promises a retry native cannot make.
- A native retry that re-submits the exit after a fresh read: the row format lets no row of another request follow an unsettled row, and same-request rows must share owner digest, PID and session, so a later process cannot settle an earlier one's row. A reconciliation row is a journal format change that the package readers and the managed handback also read.
- Recovering managed's RestorePending journal natively: it means rewriting `hyperboost-ownership.json`, which the journal decision (option b) rules out.
- Reading the pad in the thermal policy closure: the rig's pad probe is scripted per gate observation, so a status would consume a request's observations; in production it would scan HID on the dispatcher at every status.

## Watch out
- The wait for the observer before the Stop cleanup predated H3b: `ObservedStatus::drain` waited without a bound for the whole observation (Windows power, HID metadata, process inventory), and `retire` ended it only with the cleanup skipped. H3b added the pad scan and journal read after the observer's last cancel check, which made a stall likelier.
- A test filter of `stalled` matches every `installed::` test. Name the filter after the test.
- A drain bound cannot be tested with an observer that never returns: without the bound the test hangs instead of failing. Cap the test observer's hold.
- The managed codec refuses a status whose `hyperBoostBlockingControllers` is not an ordinally sorted, distinct subset of `potentialControllers` (`IpcMessageCodec.cs` 523-538), with at most 16 entries. Keep the filter if the potential list ever changes after the observation.
- The dispatcher runs a mutation for a caller that is not the active user and only then drops the reply (`serve_mutation`), so each runtime needs its own caller gate; `request_as` cannot test it, `source.mutate` can.
- A new request variant must also be added to the mutation list in `serve_connection` and to `mutation_budget`, or it is answered as a status read.
- `hyperboost_journal::restore_pending` reads the state member only. Only `relinquished` reads a document exactly as managed does.
- The rig's status probes (`HyperBoostProbes::status_probes`) are uncounted and unscripted on purpose.
