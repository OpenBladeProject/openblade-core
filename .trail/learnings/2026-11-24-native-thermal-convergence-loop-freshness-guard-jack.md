---
title: "Native thermal convergence loop: freshness, guard and test rig traps"
author: Jack
date: 2026-11-24
branch: feat/native-thermal-convergence-loop
---

## Decided
- The native convergence loop (`thermal_convergence_loop.rs`) evaluates only a reading newer than the last one it evaluated (`thermal_drift::Tracker::readings`). The 15 s D5 timer and a due D7 retry only ask for a reading; the next tick evaluates it. A retained status reading is never evidence for a reservation or a contradiction.
- Each step's journaled intent is admitted by the attempt's `Guard` (`thermal_drift::Scope`): ledger still holds the reservation, HyperBoost journal bytes unchanged and safe, Maintain admitted, not stopping. One refusal refuses the rest of the attempt.
- Disposition: all confirmed -> Confirmed; possible submission with the journal row still pending -> ReconciliationUnavailable, settled without match -> External; refused before submission for a clearable cause -> kept for one D7 retry; stop -> kept for read-only reconciliation.

## Tried and ruled out
- `Snapshot::has_fresh_mailbox_read` as the freshness signal: it is false in published statuses, so every evaluation stopped.

## Watch out
- The rig's Storage reads the ledger once at open: editing `thermal-convergence.json` under a running rig changes nothing until `Rig::restarted`. A budget test that edits the file without a restart passes on the tombstone alone (mutant survived).
- Selector 1 is not a preset the fixture reads back; use 5 (Silent) for an external preset.
- A restarted rig applies the plugged-in power profile (performance 1), not the Game rule, until the application is reported again.
- The first status after `Rig::restarted` has no `thermalConvergence`; take a reading and a tick first.
