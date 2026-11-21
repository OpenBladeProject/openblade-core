---
title: Narrowed deferred performance choice on Automatic fans
author: Jack
date: 2026-11-20
branch: fix/manual-to-automatic-fan-restore
touches:
  - "src/OpenBlade.Service/ConfirmedStateDriftTracker.cs#DecideDeferredPerformance"
  - "src/OpenBlade.Service/ControlCoordinator.cs#ReconcilesPerformanceOnAutomaticFans"
  - "src/OpenBlade.Service/ServiceControlRuntime.cs#RecordDeferredPerformanceChoice"
---

## Decided

- On reporting backends without direct manual transitions (`ControlCoordinator.ReconcilesPerformanceOnAutomaticFans`), a fan request writes performance only for a pending deferred choice. The choice is a performance choice stored with the deferral message (explicit, shortcut, a clear back to the profile, any power source). It is applied once, when a fan request next leaves fans Automatic. It is applied only if no performance change (preset, Custom levels or GPU offsets) has been observed since the choice and the fan step's fresh preset is known and still the last observed one. Otherwise the choice is dropped and the result says the mode changed outside OpenBlade and was kept.
- The state is one in-memory marker in `ConfirmedStateDriftTracker` (None, Pending, Invalidated). Pending is set only by the deferral paths, and an observed performance change turns it into Invalidated. `DecideDeferredPerformance` consumes it before any write. `RestoreUnsubmittedDeferredPerformance` re-arms it only when the preset write is known not to have been submitted, so a canceled or uncertain write is never replayed.
- A newer choice that is not deferred clears the marker. So does an OpenBlade-confirmed performance write (`ConfirmIntentional(Performance, true)`).
- On unclassified power, clearing the fan override hands fans back without a performance write. The marker stays pending and the message says the choice waits.
- The marker is lost on restart. That is accepted: the result is no write.

## Tried and ruled out

- A broad "reconcile whenever fans are Automatic and the fresh preset equals a reference" rule. It wrote presets that base never wrote: over HyperBoost (mode 7 reads no preset), over changed Custom levels, after a restart, after a profile edit, as a retry of a failed choice, over a held battery preset, and after an external change away and back.
- A reference preset (`performanceAtIntent`) compared with the fresh readback. It cannot see an away-and-back change or Custom levels; the marker replaced it.

## Watch out

- With no pending choice, a fan request on these backends writes no performance, even on exit from manual fans. A profile edit made under manual fans is therefore not applied by the exit.
- Shared selectors (`ReportsPerformanceWithManualFans == false`, RZ09-0581/0528) and direct backends (029D/029F) are unchanged. Rerun a base-versus-branch transcript of those shapes after any change here.
- A choice dropped because the preset write was blocked or uncertain is not retried.
