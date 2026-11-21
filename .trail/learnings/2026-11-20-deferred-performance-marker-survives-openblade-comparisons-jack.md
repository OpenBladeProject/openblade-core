---
title: Deferred performance marker survives OpenBlade comparisons and checks the intent
author: Jack
date: 2026-11-20
branch: fix/manual-to-automatic-fan-restore
touches:
  - src/OpenBlade.Service/ConfirmedStateDriftTracker.cs
  - src/OpenBlade.Service/ControlCoordinator.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Tray.Core/MainViewModel.cs
---

## Decided

- The deferred performance marker in `ConfirmedStateDriftTracker` is invalidated only by an observed hardware change: `Observe` finding a preset or Custom levels that differ from the last observed value (`AddObservedChange`). `ConfirmIntentional` no longer touches it, in either direction. While a choice is deferred, desired and confirmed differ by design, so a desired-versus-confirmed mismatch (full transitions, the first comparison after startup reconciliation, adapter classification) is not evidence of an outside change, and a matched comparison is not evidence that the choice was applied.
- The marker records the deferred preset and its Custom levels. `DecideDeferredPerformance` takes the intent that the fan request resolved. If that intent no longer equals the recorded choice (an application profile, a power change or a profile edit replaced it), the result is `NoLongerApplies` with "The stored performance choice no longer applies.", never the "changed outside OpenBlade" message. A persistent override that is unchanged across a power change still applies. A fresh readback that already holds the choice settles it with no write.
- Only applying the choice, or a newer choice that is not deferred, clears the marker. Re-arming happens only when the exit preset step was known not to be submitted, or stopped with no submission at all (blocked by the pre-write state read). Indeterminate, I/O-failed and canceled writes are never replayed.
- The tray "Clear manual overrides" clears performance before fans. Under manual fans the performance clear is stored and the fan clear applies it, so a shared selector no longer writes the cleared override preset and then the profile preset. The tray treats that Stored result as restored only when the post-clear status confirms the desired performance.

## Tried and ruled out

- Testing the marker without `ReconcileStartupProfileAsync`. Before startup reconciliation, observations only set the baseline, so the comparison paths that destroyed the marker never ran and every round-5 test passed with the bug present.
- Treating any Stored performance clear followed by an applied fan clear as restored. Stored also means unclassified power, a lost session and other deferrals; the post-clear status check is the honest test.

## Watch out

- The tray-order transcripts (scenarios 9 to 12 of the scratch harness) give the same final state and never more writes on 029D/029F and the shared selectors. The one difference is a synthetic battery profile preset that the forced 02B6 table does not support, which `ProfileSetValidator` rejects in production.
- A choice dropped as `NoLongerApplies` leaves the held preset; the replacing intent is not written by the fan request, consistent with profile edits under manual fans.
