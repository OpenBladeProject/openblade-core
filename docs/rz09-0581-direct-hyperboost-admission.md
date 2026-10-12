# RZ09-0581 direct HyperBoost admission

This change enables direct HyperBoost entry from Silent, Balanced, Performance
and Custom on the Blade 16 (2026) RZ09-0581, SKU RZ09-05819EN4, USB 1532:02E0.
The controlled installation and physical results are recorded below.
Balanced padless entry has existing evidence. Silent and Performance passed
the controlled checks recorded below. Custom Medium/High also passed after
the initial setup failure was reconciled and reviewed.

The service uses the confirmed starting mode, sends the model's captured
HyperBoost pair, and restores the starting mode on exit. For Custom, the existing
shared restoration path also restores the saved CPU/GPU levels. The factory
preserves Custom admission and bounds every base mode to overall HyperBoost
access. Read-only construction cannot gain write access through Custom.

## Validation procedure

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
The normal direct-entry checks below cover each new base mode, including exact
Custom-level restoration. The shared lifecycle
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

## Physical results, 2026-10-12

The user approved installation and attended reversible checks on exact model
RZ09-0581, SKU RZ09-05819EN4, USB 1532:02E0. Installed source was
`57e3416187c33b79a0f958f6f8fbbcdd78b5caef`, local-development version
`65535.417.12980.35604`. All 555 installed payload files matched the candidate.
One Tray and one Session Agent ran from the installed directory.

The Razer power adapter was connected, confirmed by the user. The service
reported plugged-in power and an absent cooling pad. Fresh exact-device,
controller, power, session and thermal admission passed before each operation.
The installed baseline passed 33 checks with one unavailable GPU-temperature
warning and no failures.

| Starting mode | HyperBoost and restoration | Result |
| --- | --- | --- |
| Silent (Quiet) | Mode 7 confirmed; Silent and Automatic restored | 50 passed, one GPU-temperature warning, no failures or skips |
| Performance | Mode 7 confirmed; Performance and Automatic restored | 50 passed, one GPU-temperature warning, no failures or skips |
| Custom (CPU Medium, GPU High) | Mode 7 confirmed; exact levels and Automatic restored | 50 passed, one GPU-temperature warning, no failures or skips |

The user watched all three completed transitions and confirmed that the tray
showed HyperBoost and returned correctly, including Custom Medium/High and the
final return to Balanced. The complete original confirmed device state
and saved overrides matched a fresh final readback: Balanced, Automatic,
HyperBoost off, temporary full charge enabled, protection enabled and charge
limit 80. No external changes, unconfirmed writes or pending HyperBoost recovery
remained.

The first Custom attempt stopped before a write because the local helper
misinterpreted a null optional combination restriction as missing support.
Source review confirmed that null uses the standard well-formed combinations;
the exact RZ09-0581 fixture contains the Custom pair and CPU/GPU setters. The
helper was corrected without changing production code.

The subsequent typed Custom High/Low selection returned an indeterminate
multi-command hardware result. No HyperBoost command followed. Diagnostic
records show the initial failure at 10:15:06 UTC, then an autonomous Maintain
desired state attempt reserved at 10:15:09 and acknowledged/confirmed at
10:15:10. Later readbacks confirmed Custom High/Low, but cannot establish that
the original operation succeeded. After two stable read-only observations,
one saved-intent restoration returned Balanced/Automatic, confirmed by readback
at 10:15:48 UTC. The original battery state and overrides also matched.

Read-only source review confirmed that the first operation had observed a known
intermediate state. Unavailable confirmation would instead create a pending
confirmation and block Maintain admission. The enabled Maintain policy treated
the confirmed mismatch as drift, performed another fresh read and safety
admission, and durably reserved its bounded action. This was not a replay while
submission remained unresolved; no production change was indicated.

After original-state restoration, a fresh typed Custom Medium/High selection
applied and confirmed directly. Its new baseline passed 33 checks with the same
GPU-temperature warning. The HyperBoost round trip passed all 50 checks: an
indeterminate enable was resolved by fresh mode-7 readback without retrying the
write, and exit restored exact Medium/High levels and Automatic fans. The saved
Balanced intent was then restored and confirmed, with original battery settings
intact. No pending or unconfirmed writes remained.

The initial High/Low setup result is retained as a failed attempt; later success
does not rewrite it. Physical Custom restoration coverage is Medium/High. The
diagnostic archive and raw snapshots remain local; no raw payloads or
machine-identifying data are included here.
