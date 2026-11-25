---
title: "Native convergence review round two: reachability of the D9 and reserved-row gates"
author: Jack
date: 2026-11-25
branch: feat/native-thermal-convergence-loop
---

## Decided
- The reapply publishes the status straight after a rearm attempt (a closure passed into `reapply_changed`), before any step runs, as managed publishes AwaitingObservation after its rearm. A client disconnect drops the mutation future in `serve_mutation`, so an end-of-request publish alone left Suppressed published.
- D9's pre-reservation fan check and the fan step's own refusal share one predicate, `Runtime::holds_fan_continuity` (live owner, proof generation, custody, authentication). The status nominator keeps the liveness-only `holds_live_fan_owner`.
- The reapply's manual-fan bridge stays stricter than managed (outstanding fan row counts as manual; saved manual fans at either AC class). It is documented as a deviation, not resolved.

## Tried and ruled out
- A rig test for F4 (`planned == Fans::Unplanned` in `convergence_intent`): unreachable. Native never holds a saved Stop (profiles reject it, manual-overrides.json accepts fans 0..=3, fan requests accept 0..=3), and every other unplanned intent under a manual reading classifies ManualTopologyOnly before evaluation.
- A rig test for F8 (the pending fence in `reconcile_reserved`): unreachable in the rig. While a journal row is unsettled, observation is refused, so no base carries a confirmed reading and the HyperBoost conjunct keeps the reservation anyway.
- Saved Stop via profiles.json or manual-overrides.json: both are rejected or quarantine the configuration.

## Watch out
- `restarted()` drains the old rig, and the drain hands owned fans back, so the restarted Service has no outstanding fan row. Use a crash (drop without drain) to keep one.
- Firmware shows no preset for Balanced under manual fans ((0,1) is FixedMaximum), so selector 0 plus manual fans gives a fans-only contradiction; selector 5 (Silent) plus manual fans gives both.
- In a two-step attempt from manual fans to Automatic, the fan step runs first with two setters, then the performance step. A suspend at the first power read after the second setter defers the performance step: the refused-last Blocked path.
- `Runtime::read_low_power` can be set once and `wired()` already sets it; suspend through `custody.lifecycle.ingress` from a port hook instead.
