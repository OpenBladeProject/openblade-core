---
title: "Native request-trigger reconciliation, round-1 fixes"
author: Jack
date: 2026-11-22
branch: feat/native-profile-request-triggers
---

## Decided
- Request-driven reconciliation lives behind `service-profile-triggers-v1`; no packaged composition implies it, and both package readers refuse it by feature name and by build.rs marker.
- Each reconciliation step journals under `step_request(trigger, step)`: SHA-256 of a fixed domain, the lowercased trigger id and the step name, written as a version 8 GUID. The journal refuses a second row chain under one id, so a shared id silently dropped the fan step of a preset+Fixed rule.
- Reports and saves that may reconcile run under `RECONCILE_BUDGET` (11 s, const-asserted against 38 exchanges at the 2 s/15 frame pace) and are never canceled at the deadline by `serve_mutation`; the Agent waits budget + one frame. The managed tray's updateProfiles has no reply deadline.
- A pair stays owed until a reconciliation submits or confirms nothing to apply (`Reconciled::settles`); a refusal that submitted nothing leaves startup owed.
- Fans-first reads `MailboxSource::confirmed_manual_fans` (the retained thermal observation) or `storage.outstanding_fan()`.

## Tried and ruled out
- Ordering by `publisher.borrow().active.confirmed_manual_fans()`: the published status is rebuilt from observed metadata and carries no firmware fans, so it was always false; tests passed only through `outstanding_fan`.
- `assert_ne!(rig.get()["status"]["confirmedState"]["fans"], 0)` is vacuous: getStatus confirms nothing, so the value is null. Use an `observeState` request after a getStatus probe.

## Watch out
- Equivalent mutants in this area: the perf and fan steps' unclassified-reconcile refusals each cover the other (the second step always runs and says the same message); `submission_possible` is subsumed by the step-id journal check except under a poisoned lock; the fan step's `verify_overrides` is backed by `current_override`'s own drift check; `ran &&` cannot change a result because a step that fences the store never confirms.
- The fixture's per-job paused clock never exercises the combined two-step budget end to end; only the const assert does.
