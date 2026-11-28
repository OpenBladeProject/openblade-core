---
title: Native thermal startup ends per user after a compared reading
author: Jack
date: 2026-11-23
branch: feat/native-thermal-drift-tracker
---

## Decided
- Native thermal startup (`triggers::Startup`, `Runtime::startup`) is keyed on the SID, as managed keys activation. A new session of the same user is not starting again and compares drift.
- Startup ends only once a starting reconciliation compared a fresh reading (`Transition::compared`; an observed HyperBoost compares nothing) and a debt of that user then settles, or once a competing controller drops a cause (`Runtime::drop_for_controller`, taken by the lifecycle, report and save captures). A debt that settles with nothing compared, such as unclassified-adapter refusals or saved intent that reads no firmware, leaves the user starting, so the next reconciliation overwrites. Managed keeps startup pending on unclassified power (`ReconcileStartupProfileCoreAsync`).
- An overwrite (startup or activation) whose intent has no destination, such as a GPU clock effect, owns the preset it leaves in place. Without that, every later intent paused with externalChanges [0] forever.
- After a controller-dropped startup the next power change pauses unless firmware shows its intent. Managed's transition pauses there as well (its prior desired state is the unapplied startup intent); only its convergence loop repairs it. The pause is kept on purpose.
- A performance pair written from manual fans returns them to Automatic: the step records its baseline reading and `Tracker::submitted_from` expects Automatic fans too, matching managed `ConfirmedCoupledWrites`.

## Tried and ruled out
- Ending startup when a decision was made, without waiting for the debt to settle: a retry after a transient write refusal would then compare WithPrior with nothing owned and pause.
- Emulating the old per-pair startup with `!debts.contains_key(pair)` as a mutant: the lifecycle owes the pair before it reconciles, so the mutant survives. A key of SID plus session is the faithful mutant.

## Watch out
- Fans with no native recipe (Unplanned) on a manual reading are not adopted by an overwrite: they recover once firmware fans return to Automatic, unlike a preset, which recovers only if firmware lands on the exact new intent.
- A save under a competing controller is refused at the write capture outside low power, although the profile_mutation comment says storing consults no controller admission. This predates the drift work.
