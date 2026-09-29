# Cooling-pad fan acquisition and recovery decision

Tracking: [issue #119](https://github.com/OpenBladeProject/openblade-core/issues/119).
Related: [developer validation PR #624](https://github.com/OpenBladeProject/openblade-core/pull/624)
and [evidence PR #33](https://github.com/OpenBladeProject/openblade-captures/pull/33).

## Current decision

No complete reversible fan contract is established while vendor recovery is
unavailable. The successful raw control-state round trip does not close that gap.
Do not execute a fan writer or replay the old standalone Auto release. Synapse
and the two stopped Razer services remain stopped at the user's request.

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

## Manual recovery decision before implementation or execution

The user must decide whether a future exploratory trial may use vendor-assisted
recovery if necessary. No recovery action is performed by this document or by the
existing mode/query tooling. Keeping vendor controllers stopped remains the
current instruction.

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
review the bounded typed experiment only after its recovery constraints are
resolved, then show the exact action before a fresh attended trial.

Another ordinary vendor Auto/Fixed capture or an opaque-byte query would not
resolve this known gap. Further evidence should target the acquisition context
and the missing restoration path. Until then, keep fan mutation disabled.
