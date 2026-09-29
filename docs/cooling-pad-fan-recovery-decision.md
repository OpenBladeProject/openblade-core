# Cooling-pad fan acquisition and recovery decision

Tracking: [issue #119](https://github.com/OpenBladeProject/openblade-core/issues/119).
Related: [developer validation PR #624](https://github.com/OpenBladeProject/openblade-core/pull/624)
and [evidence PR #33](https://github.com/OpenBladeProject/openblade-captures/pull/33).

## Live attempts on 2026-09-29

The first attended execution from reviewed source `45a4aa6e` reached raw `0300`
and acknowledged Medium Fixed `01012C`. The operator observed Medium and later
confirmed normal unchanged green lighting. The 180-second physical-confirmation
wait expired while the coordinating conversation was compacted, before that
reply was submitted. The command stopped at `fixedPhysicalConfirmation` with
`OperatorTimeout`; it sent neither Auto nor raw-mode restoration. This is an
incomplete attended experiment, not evidence of an abnormal physical response.

The operator restored Auto through Synapse and confirmed normal green lighting
after normal exit. A separately requested new attempt used the same reviewed
build. It read raw `0300` and stopped with `BaselineNot0000` before any setter.
Vendor-assisted physical Auto recovery therefore did not establish raw `0000`
in this session. Both Razer services were subsequently stopped as authorized.

A bounded raw-mode restoration path is being prepared for independent review.
It must preserve both original results, check the current raw value before a
single `0000` setter, confirm the exact echo and readback, and obtain a separate
physical check. It must send no fan or lighting command and perform no vendor
restart. Further fan execution requires that restoration and a fresh baseline.
Production, fan-state, ownership and handback admission remain closed.
## Bounded raw-mode restoration command

The separate developer command restores raw control state after the incomplete
fan trial. It does not recover or confirm a fan setting:

```powershell
dotnet run --project src/OpenBlade.Capture --no-build -- restore-rz09-0528-cooling-pad-control-state --confirm-target RZ09-05289EN4:1532:0F43:0200:0300-0000
```

Exact arguments are checked before native construction. The existing strict
model/SKU, pad revision, geometry and competing-controller guards apply. Three
transactions are reserved before access. A fresh raw `0300` permits one
`00/04:0000` setter, its exact acknowledgement, then a `00/84` readback of `0000`.
A fresh `0000` skips the setter. Unknown replies and any exchange failure stop
further writes. A separate attended physical confirmation is required in both
cases. Exchanges, observation and disposal retain their three-second,
180-second and five-second bounds; there are no retries or finally writes.

This command sends no fan or lighting setter and performs no process or service
operation. The user's current instruction forbids agent-initiated Synapse use.
Raw restoration and physical normal behavior do not establish fan-state,
ownership, handback or production semantics. Preserve both incomplete results
and the recovery result before starting any fresh full fan trial.
## Current decision

No complete independently reversible fan contract is established. The successful
raw control-state round trip does not close that gap. The user has authorized
attended recovery if needed, permitting preparation of the exploratory test below.
Do not treat it as an admitted fan writer or replay the old standalone Auto
release. Synapse and the two stopped Razer services remain stopped unless the
authorized recovery contingency is needed.

The exact developer target remains RZ09-05289EN4 plus pad `1532:0F43`, revision
`0200`, MI_00, usage `000C:0001`, 91-byte Windows Feature buffer with prefix zero.
BIOS versions are provenance only. No laptop command or laptop mode semantics
are transferred to the accessory.

## Evidence that determines the decision

- The current-host vendor round trip establishes Medium Fixed `0D/10:01012C`
  and Auto `0D/10:000600` encodings and operator-observed fan changes.
- The current-host Fixed exit/reopen capture contains `00/04:0000` without a fan
  setter at exit. Fixed behavior persisted. This raw mode value therefore cannot
  serve as an Auto restoration command.
- The direct mode-only trial confirms raw `0000 -> 0300 -> 0000`, exact echoes
  and readbacks, with normal unchanged physical behavior. It sent no fan setter.
- The earlier direct fan trial acknowledged Fixed and `000600`, but left the pad
  in flashing-red sleep. USB-only reconnect failed; both USB and 12 V had to be
  disconnected. Command acceptance did not establish safe restoration.
- The earlier paired Auto-state plus host-demand trial physically worked once,
  but recovery used Synapse. It does not supply an independent Auto handback.
- `0D/81` is an opaque observation, `0D/90` describes stored banks, and the speed
  candidate is unsupported. None supplies active fan or tachometer confirmation.

Exact evidence files in the captures repository:

- `2026-09-29-rz09-0528-cooling-pad-vendor-auto-round-trip.json`
- `2026-09-29-rz09-0528-cooling-pad-vendor-fixed-exit-reopen.json`
- `2026-09-29-rz09-0528-cooling-pad-control-state-round-trip.json`
- `2026-08-09-rz09-0581-cooling-pad-direct-write-negative.json`
- `2026-08-09-rz09-0581-cooling-pad-host-control-physical-positive.json`

The August records identify the same pad, but are not current-host fan admission.
Current vendor startup contains additional initialization. Nothing yet proves
that setting raw `0300` alone supplies the context missing from the failed trial.

## Smallest candidate to review

This is an exploratory hypothesis, not an admitted reversible validation:

1. Fresh exact-device/controller checks, then `00/84:0000` and save raw `0000`.
2. `00/04:0300`, require exact acknowledgement, then query and confirm `0300`.
3. One `0D/10:01012C`, require exact acknowledgement and attended physical fan
   confirmation. Do not report the echo as target or RPM readback.
4. One `0D/10:000600`, require exact acknowledgement and attended physical return
   toward the saved quiet baseline. Its safety in this context remains unknown.
5. `00/04:0000`, require exact acknowledgement, then query and confirm raw `0000`.
6. Separately confirm normal fan, unchanged full-brightness static-green lighting,
   and no flashing-red or sleep indication.

No arbitrary commands, alternate IDs, retries, repeated demand, guessed speed
ranges or lighting writes belong in this candidate. Ambiguous submission or
readback stops further writes. Raw mode restoration must not conceal failed fan
restoration. A timeout or absent physical confirmation must not trigger an
unconditional Auto cleanup. Production, fan-state, ownership and handback gates
stay closed even if this single experiment succeeds.

## Authorized recovery contingency

On 2026-09-29, the user explicitly allowed attended full USB-plus-12 V power
cycling and temporary restoration of the two named Razer services and Synapse
if recovery is needed. This resolves the contingency for the proposed experiment.
It does not request routine vendor restarts or authorize the first fan trial.
No recovery action is performed automatically by the developer command.

The demonstrated recovery options have limits:

- In the paired Auto-state plus host-demand trial, restarting the two vendor
  services and launching Synapse restored the operator-confirmed Auto UI. This
  is vendor-assisted recovery, not an OpenBlade restoration primitive or proof
  that ordinary startup recovers the flashing-red failure.
- The flashing-red sleep failure needed an attended full USB-plus-12 V power
  cycle; USB alone was insufficient. The evidence does not establish that power
  cycling alone restores today's full Auto/static-green baseline while Synapse
  and its services remain stopped.

A permitted recovery plan must name these contingencies and save the preferred
Auto/full-brightness/static-green settings before any fan mutation. Permission
for recovery would not authorize automatic vendor restarts, production admission,
additional service stops or the fan trial itself. Implement and independently
review live hardware wiring only after its recovery constraints are resolved,
then show the exact action before a fresh attended trial.

Another ordinary vendor Auto/Fixed capture or an opaque-byte query would not
resolve this known gap. Further evidence should target the acquisition context
and the missing restoration path. Production fan mutation remains disabled.

## Attended command boundary

The original offline prototype has a separately guarded developer CLI path. It
uses the existing strict exact-device pad actor, prefix zero, and fresh competing-
controller checks before each exchange. BIOS remains provenance only. The command
requires these exact model/action and recovery acknowledgements:

```powershell
dotnet run --project src/OpenBlade.Capture --no-build -- validate-rz09-0528-cooling-pad-fan-exploratory-trial --confirm-target RZ09-05289EN4:1532:0F43:0200:Medium2200-Auto --confirm-recovery ManualPowerCycleAndVendorRecovery
```

Missing, altered or extra arguments must not invoke the runner or open a device.
No arbitrary report ID, command, target or fan demand can be supplied. The
command performs no vendor process or service operation and no lighting write.

The runner reserves seven transactions before opening a session. It requires
exact raw baseline `0000`, exact mode `0300` acknowledgement/readback, and exact
three-byte Medium and Auto setter echoes. It requests separate physical
observations after Medium and Auto, then confirms raw-mode restoration and a
final physical observation. Prompts go to standard error; standard output is the
structured result. Only type `YES` after the relevant operator confirmation.
Never pre-fill observations or infer them from command acknowledgements.

Every failed exchange, unexpected echo, rejected observation, timeout, exception
or cancellation stops further writes. There is no unconditional Auto cleanup,
mode cleanup or retry. Exchanges use a three-second bound, each operator wait a
180-second bound, and disposal a five-second bound. Noncooperative observer and
cancellation handlers do not block the runner or resume the sequence later.
Incomplete mutation is reported as requiring manual recovery rather than claimed
restored. Physical observations never become target, RPM, ownership or firmware
fan-state readback. Production and handback admission stay closed.

## First execution checklist

Before the first fan write, finish tests and independent review of the actual CLI
and native wiring, build the exact committed source and record its hash privately.
Show the complete exploratory sequence and obtain the present operator's current
baseline confirmation: Auto, full-brightness static green, normal physical fan,
pad-dependent HyperBoost inactive, USB and 12 V connected. Record fresh exact-pad
inventory and zero competing controller/capture processes. No USBPcap may run
against the interface during the trial. Run only one attempt from that reviewed
build, with a private create-new start sentinel to prevent accidental reexecution.

At each of the three prompts, obtain a current operator response before supplying
input. On any abnormal fan, lighting, red/sleep indication or failed exchange,
stop further writes and preserve the result. Use only the authorized attended
recovery if needed: no guessed report, repeated Auto release or automatic restart.
If full power cycling is required, it is a manual action; restore the saved
preferred settings through the authorized vendor recovery and confirm the outcome.
If the experiment succeeds, leave vendor software stopped as requested.

The live attempts above are incomplete. Test-double success validates control
flow only, not device restoration. The known negative Auto release remains evidence.

CLI preparation verification: all 585 Capture tests and three architecture tests
passed, including 48 exploratory-runner cases and 22 CLI-admission cases.
Independent review passed for exact argument admission, deferred native factory,
strict pad identity/controller checks, JSON/prompt separation and stopped-write
behavior. The live attempts and vendor-assisted recovery are recorded above.
