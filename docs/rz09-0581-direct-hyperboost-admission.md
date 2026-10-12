# RZ09-0581 direct HyperBoost admission

This change prepares direct HyperBoost entry from Silent, Balanced, Performance
and Custom on the Blade 16 (2026) RZ09-0581, SKU RZ09-05819EN4, USB 1532:02E0.
Keep the candidate admission in draft until the validation below is recorded
and reviewed. A controlled local installation is needed for these checks.
Balanced padless entry has existing evidence; Silent, Performance and Custom
direct entry remain unvalidated on this model.

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

## Lifecycle coverage

The existing RZ09-0581 [crash-recovery annotation](../captures/annotations/2026-08-21-rz09-0581-hyperboost-padless-crash-recovery.json)
and [low-power annotation](../captures/annotations/2026-08-21-rz09-0581-hyperboost-padless-low-power-recovery.json)
record Balanced/Automatic restoration, stable repeated readback and journal
retirement. Unexpected exit, shutdown and low-power restoration share
`RestoreOwnedHyperBoostForLifecycleCoreAsync` and `RestoreHyperBoostIntentAsync`.
Silent and Performance use that same checked path with their saved base mode.

Custom also restores the saved CPU/GPU levels. Regression tests cover Custom
unexpected-exit and shutdown restoration, plus explicit recovery when the
base mode has returned but the observed levels differ from the saved levels.
The remaining physical check is a normal direct-entry round trip for each new
base mode, including exact Custom-level restoration. The shared lifecycle
coverage does not require another crash or sleep test absent a new uncertainty.

## Targeted round trip

After an approved local installation, select one starting mode in the tray,
then save a fresh green baseline with the matching developer tool. Run only the
HyperBoost check:

```powershell
OpenBlade.FirmwareSmoke.exe baseline --output C:\tmp\ob-717-silent-baseline
OpenBlade.FirmwareSmoke.exe hyperboost-round-trip --baseline C:\tmp\ob-717-silent-baseline\baseline.json --confirm-target RZ09-0581-FIRMWARE-SMOKE --output C:\tmp\ob-717-silent-round-trip
```

Use separate report directories for Silent, Performance and Custom. The command
requires a green unchanged baseline and tests only the current confirmed mode.
It records mode-7 readback and exact restoration, including Custom CPU/GPU
levels. A failed or skipped admission returns failure. Stop after a failed run
and reconcile read-only before taking any further action.

Save the initial installed settings before selecting the first mode. Restore
those settings after the final successful check and confirm them by readback.
The reports are local developer evidence; sanitize the relevant model, source,
power/pad state and restoration results before committing them.
