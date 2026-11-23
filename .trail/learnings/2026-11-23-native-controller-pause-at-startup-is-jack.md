---
title: "Native controller pause at startup is stricter admission, not managed parity"
author: Jack
date: 2026-11-23
branch: feat/native-thermal-drift-tracker
---

## Decided
- Supersedes the parity point of 2026-11-23-native-thermal-startup-ends-per-user-jack.md. Managed has no competing-controller refusal on startup or ordinary thermal transitions: `ApplyFullTransitionAsync` mentions controllers only for lighting and the resume-hold term, and `ScopedThermalWriteBlockReason` has no controller clause. Its controller gates cover fan-curve maintenance, fan recovery and convergence only. Managed's startup under a controller writes the saved profile.
- The pause after a controller-dropped startup (`Runtime::drop_for_controller`) is native-only: native refuses every write under a controller. The user sees externalChanges [0] with nothing changed outside. It heals when a later intent matches firmware, otherwise it waits for an explicit request. A controller-detection failure or an inventory slower than 1 s (controller_presence/admission.rs) gives the same denial with no controller present.
- A native save outside low power takes the write capture, so a controller, a locked session or an unknown AC status refuses it; managed stores profiles without admission. This is a documented deviation.

## Tried and ruled out
- Reading `powerToBatteryWithController` (externalChanges [0], submitted false) as managed controller gating: it is drift against the desired state managed publishes before the transition reads it as prior.

## Watch out
- If saves are relaxed later, a controller refusal of a step's own authorization inside `reconcile_locked` does not take `drop_for_controller`; it spends a retry.
- An unowned observed HyperBoost compares nothing, so startup stays pending and the next comparing cause overwrites a change recorded in between, where managed would compare.
