# Cooling-pad fan validation

Tracking: [issue #119](https://github.com/OpenBladeProject/openblade-core/issues/119).
Evidence reviewed against product `8c0ebe95` and captures
`4f2bf1565c7557439760db7e45104bb1e8b62bfd` on 2026-09-29.

## Current scope

Laptop HyperBoost has shipped independently: RZ09-0581 padless support and
RZ09-0528 pad-present and padless support use their own admitted paths. The
remaining accessory work is pad fan control, Smart Fan Curve and the opt-in
demand API, pad lighting synchronization, and the unshipped RZ09-0581
pad-attached HyperBoost path.

[Draft #125](https://github.com/OpenBladeProject/openblade-core/pull/125) contains
candidate implementations. Extract independent slices onto current `main`;
reuse the current thermal executor and ownership recovery for laptop changes.
Keep accessory writes closed while their evidence gates are incomplete.

## What the pad evidence establishes

The August captures concern RZ09-05819EN4 BIOS 4.00 and the exact cooling pad
`1532:0F43`, revision `0200`. Its MI_00 control collection uses usage
`000C:0001`, 91 feature-report bytes, report ID `05`, and a 90-byte Razer body.
MI_01 input collections are separate.

| Observation | Supported conclusion |
| --- | --- |
| `0D/81`, request `010500` | Third response byte is opaque; no target, RPM, active mode, or ownership meaning is admitted. |
| `0D/90`, bank selectors 0 and 1 | Stored configuration banks, unchanged across physical preset transitions. |
| `0D/88`, request `010500` | Unsupported current-speed candidate; no tachometer readback. |
| `0D/10:000600`, immediately followed by `0D/01:01052C` | One host-demand 2200 trial produced an operator-confirmed fan response. Restoration used Synapse. |
| Standalone `0D/10:000600` after a Fixed write | Acknowledged, but the pad entered flashing-red sleep and required a USB-plus-12 V power cycle. |

The standalone command is not a validated release or restoration operation.
An acknowledgement proves acceptance, not the fan state. Synapse Auto UI
recovery is not an OpenBlade handback. Continuous lighting frames and the prior
Synapse process session were excluded as prerequisites for vendor UI
transitions; retained service, driver, or device state was not excluded.

Sources:

- [Protocol observations](../captures/decoded/rz09-0581-pid-02e0-bios-4.00-cooling-pad-protocol-observations.json)
- [Direct-write negative result](../captures/annotations/2026-08-09-rz09-0581-cooling-pad-direct-write-negative.json)
- [Host-control physical response and vendor recovery](../captures/annotations/2026-08-09-rz09-0581-cooling-pad-host-control-physical-positive.json)
- [Vendor context isolation](../captures/annotations/2026-08-09-rz09-0581-cooling-pad-fan-context-isolation.json)

Recalculate hashes against the selected capture commit before extracting an
admission manifest. Some draft #125 source hashes no longer match the retained
annotations; copying the old manifest does not establish admission.

## Next attended session: vendor baseline on RZ09-0528

A metadata-only inventory on 2026-09-29 found RZ09-05289EN4 BIOS 2.02 and the
exact revision-0200 pad with the expected MI_00 geometry. This verifies present
identity, not protocol semantics or write admission on this host.

Prepare one vendor-controlled Fixed-2200 transition and restoration session:

1. The operator confirms the pad is powered, its fan behaves normally, Synapse
   can control it, and USB and 12 V connections are accessible. Save the current
   fan mode, preset settings, lighting settings, and OpenBlade process/service
   state. If the initial mode is not Auto, establish an operator-confirmed Auto
   baseline and retain the original settings for final restoration.
2. Re-run `captures/tools/Get-CoolingPadCaptureInventory.ps1`. Identify the
   pad's current address and root in USBPcap's capture inventory; a Windows PnP
   address is not sufficient. Verify the capture-plane address immediately
   before starting. Record host, pad, firmware, controller version, and settings.
3. Use the existing generic
   `captures/tools/Invoke-InteractiveUsbPcapCapture.ps1` with the verified root,
   pad device address, a new private output directory, and a bounded timeout.
   It requires an attended administrator terminal and manages the OpenBlade
   service around capture. Confirm capture readiness and the stopped service;
   run no OpenBlade device-control process during capture. Leave vendor
   controllers under operator control.
4. Record timestamped markers for the Auto baseline, selection of Fixed 2200,
   physical fan response, return to Auto, and restoration of original settings.
   Preserve UI confirmation separately from physical observation and protocol
   acknowledgement. Observe vendor demand updates after the return to Auto.
5. Stop through the wrapper's stop sentinel and verify graceful capture
   shutdown, original pad settings, normal physical operation, and original
   OpenBlade process/service state. If recovery is uncertain, stop validation
   and let the operator use the vendor recovery flow. Do not replay commands.

The older `Invoke-CoolingPadFanContextCapture.ps1` is explicitly scoped to the
0581. Do not remove its identity checks to run it on the 0528. The generic
wrapper requires a separate operator marker log; it does not produce the
specialized context analyzer's marker sequence automatically.

Decode only the selected pad traffic. Publish a sanitized annotation with
request/response correlation, observation intervals, controller continuity,
physical outcome, and restoration outcome. Validate it with
`captures/tools/Test-CaptureEvidence.ps1` against the private raw artifact.
Keep raw PCAP, process traces, instance IDs, serials, and local paths private.

## Decision after capture

First determine whether Auto requires continued host demand and which complete
vendor transition returns control safely. If autonomous pad operation or an
independent state observation remains unresolved, retain `ObservedOnly` and
continue vendor-side analysis. Do not substitute zero from `0D/81` for proof.

Only after a reviewed restoration contract exists, prepare a narrow typed
2200 acquisition/handback trial with saved confirmed state, fresh exact-device
and ownership checks, bounded execution, independent confirmation, and no
retry after an indeterminate submission. Do not start with the old
500/3200 range validator or its presumed standalone release.

Range, cadence, disconnect/reconnect, shutdown/crash, HyperBoost interlocks,
physical-button ownership, and lighting lifecycle are later gates. Sleep/resume
is the final physical-validation step and requires fresh approval from a
present operator. Pad lighting needs its own reversible frame and brightness
lifecycle evidence before keyboard mirroring is admitted.
