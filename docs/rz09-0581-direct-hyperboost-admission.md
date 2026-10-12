# RZ09-0581 direct HyperBoost admission

This change prepares direct HyperBoost entry from Silent, Balanced, Performance
and Custom on the Blade 16 (2026) RZ09-0581, SKU RZ09-05819EN4, USB 1532:02E0.
The candidate admission is not ready to merge or deploy until the validation
below is recorded and reviewed. Balanced padless entry has existing evidence;
Silent, Performance and Custom direct entry remain unvalidated on this model.

The service uses the confirmed starting mode, sends the model's captured
HyperBoost pair, and restores the starting mode on exit. For Custom, the existing
shared restoration path also restores the saved CPU/GPU levels. The factory
preserves Custom admission and bounds every base mode to overall HyperBoost
access. Read-only construction cannot gain write access through Custom.

## Required before merge

- Confirm direct Silent, Performance and Custom entry and exact restoration on
  this laptop through captured, typed, reversible operations. Save the confirmed
  baseline, pass fresh exact-device, controller, power and pad checks, apply the
  captured mode-7 pair, confirm both channels, restore, and confirm restoration.
- Record the power connector and pad state. Both pad routes use the shared
  transition and restoration handler; one round trip per new base mode validates
  that path. Add a second pad state for concrete eligibility or journaling
  uncertainty. Verify Custom CPU/GPU levels as well as mode.
- Stop further writes if submission or readback is ambiguous. Reconcile
  read-only. Do not retry an uncertain write or guess the restoration state.
- Review whether existing crash-recovery and low-power evidence covers each new
  restoration target. Record the source evidence supporting any reused coverage.
  Any needed sleep/resume test requires fresh approval from a present user.
- Complete an installed tray round trip and record the exact source revision and
  restoration results before marking the PR ready.

All enable and restoration reports already exist in this model's trusted
fixtures. New vendor capture is needed only if a concrete protocol uncertainty
remains; repository guidance permits direct typed validation of captured
commands. Never substitute RZ09-0528 evidence for this model's physical results.
The RZ09-0528 portion of issue #609 was completed by PRs #596 and #610.
