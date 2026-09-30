# Cooling-pad fan validation

Tracking: [issue #119](https://github.com/OpenBladeProject/openblade-core/issues/119).
Evidence reviewed against product `8c0ebe95` and captures
`4f2bf1565c7557439760db7e45104bb1e8b62bfd` on 2026-09-29.

## Completed exploratory sequence on 2026-09-29

Following a separately reviewed raw `0300 -> 0000` restoration, the full attended
trial completed from developer source `1c19debf8b712667822093dc2f9e428e4ff54171`.
Typed results confirm raw `0000 -> 0300 -> 0000`, exact Medium `01012C` and Auto
`000600` setter acknowledgements, and all three separate physical confirmations.
The operator reported normal Medium, return to quiet Auto, and normal unchanged
final behavior, with full-brightness static-green lighting and no red/sleep.
No Synapse/service restart, lighting setter, demand update, power cycle or retry
occurred during this completed attempt. Vendor software remains stopped.

One fresh read-only query 131.58 seconds after trial-process exit still returned
raw `0000`. The operator subsequently confirmed quiet Auto and normal unchanged
green lighting, with no red/sleep. This is a normal-exit persistence observation;
disconnect, crash, sleep and autonomous recovery remain unvalidated.

The result is typed evidence and operator observation, without independent PCAP
or retained native report traces. It establishes one physically successful fixed
sequence on this host; it does not establish RPM/firmware state readback, causal
mode semantics, ownership or production handback. Prior vendor recovery may have
left state not excluded by this experiment. Preserve the earlier negative Auto
release and both incomplete attempts. Next prepare offline lifecycle, controller
conflict, disconnect and shutdown/crash recovery cases before any further
attended lifecycle trial. Accessory production admission remains closed.
## Completed passive USB reconnect

After the successful fan trial and normal-exit check, one attended USB-only
reconnect from quiet Auto completed with 12 V retained by instruction. Fresh
exact-pad metadata and separate guarded read-only sessions before and after
returned raw `0000`, with exit zero. The operator reported no fan/lighting change
and confirmed normal quiet Auto/full-brightness static-green behavior after the
query. No setter, vendor action, retry or recovery occurred. Timing and retained
power are operator/instruction provenance, not independently instrumented facts.

This establishes one passive reconnect from the restored baseline. It does not
establish recovery from interruption while Fixed is active, crash, sleep or
power loss, nor independent fan state/ownership or production admission. Prepare
those distinct cases with explicit interruption state and recovery before any
new attended action. See draft #624's USB reconnect checklist and capture #33.
## Completed handle continuity

The attended same-process continuity trial from developer source `1baf4d6a`
completed with exit zero. Medium and normal green lighting were physically
confirmed before the first actor closed completely. A fresh exact-device session
then read raw `0300`, acknowledged Auto with physical return to quiet behavior,
and restored/read-back the original raw `0000`. The final physical check confirmed
normal unchanged green lighting without red/sleep. No vendor recovery, service
action, retry, lighting setter or power cycle occurred.

This establishes one normal-close/fresh-handle experiment in the same process.
New-process continuation and crash recovery remain distinct unresolved boundaries;
retain the original Auto/raw `0000` baseline and bind future continuation to the
just-created trial. Firmware fan state/RPM, ownership and production admission
remain unvalidated. Source verification passed all 657 Capture tests, three
architecture tests and independent review.
## Earlier incomplete exploratory attempts on 2026-09-29

The reviewed developer trial in draft #624 acknowledged raw `0300` acquisition
and Medium `01012C`. The operator observed Medium and normal unchanged green
lighting, but the 180-second confirmation expired before the reply reached the
command during conversation compaction. No Auto or raw-mode restoration was
sent. This incomplete experiment does not establish either failure or success
of the proposed Auto restoration.

The operator recovered physical Auto through Synapse and confirmed normal green
lighting after exit. A new expressly requested attempt then read raw `0300` and
stopped before every setter because its required baseline is `0000`. Physical
Auto recovery and raw-mode restoration are separate observations. The bounded
raw-mode restoration and subsequent full trial are recorded above. Neither
attempt establishes fan readback, ownership, handback or production admission.
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

## Completed vendor baseline on RZ09-0528

A metadata-only inventory on 2026-09-29 found RZ09-05289EN4 BIOS 2.02 and the
exact revision-0200 pad with the expected MI_00 geometry. This verifies present
identity, not protocol semantics or write admission on this host.

The attended session is recorded in
[capture PR #33](https://github.com/OpenBladeProject/openblade-captures/pull/33).
Starting from Fixed Medium 2200, the operator selected Auto, observed fan
ramp-down, then restored Fixed Medium 2200 with normal fan response and
unchanged full-brightness static-green lighting. Both vendor pairs have matching
status-02 semantic echoes and checksums. Medium restoration is `01012C`, distinct
from the historical High-2200 payload `01022C`.

The 44.87-second Auto interval contains no host-demand updates or fan queries.
Green lighting frames continue approximately every five seconds. Raw USB
SET_REPORT/GET_REPORT uses selector `0300` on interface zero, with a 90-byte
body; this does not validate Windows user-mode feature-report framing. Preserve
that distinction from the historical report-ID-05 path.

Capture stopped gracefully and OpenBlade restarted without errors. After
capture, the operator selected Auto again for quietness. That preference is
preserved; it is outside the captured restoration evidence.

A second attended session captured normal Synapse exit and reopening from
Auto. The operator noticed no fan or lighting change and confirmed unchanged
Auto, full brightness and static green after reopening. Exit correlates with
one acknowledged `00/04:0000`; startup includes acknowledged `00/04:0300`,
two `00/84` queries returning `0300`, and two acknowledged Auto commands.
These are accessory-specific control-state candidates with unassigned meaning.

No complete Razer report occurs for 133.52 seconds between exit acknowledgement
and startup. Three snapshots found zero AppEngine processes, while three vendor
services remained running. Lighting frames pause for 137.77 seconds, then carry
a brief green intensity ramp before steady full-green updates. That encoded
ramp was not reported as a physical fade. All 173 complete bodies pass checksum
validation; capture stopped gracefully and OpenBlade restarted without errors.
Serial-bearing startup replies remain private.

Application exit does not establish independent firmware ownership, and the
observed mode-family pair is not an admitted OpenBlade release. The historical
negative standalone Auto result remains relevant. These sessions are vendor
observations, with all OpenBlade accessory mutation gates closed.

A third session repeated exit/reopen after vendor Fixed Medium 2200 selection,
then restored preferred Auto. Exit/startup uses the same `00/04` values as Auto.
The operator noticed no physical change at exit and reported that Synapse still
showed Fixed after reopening. Eight Medium pairs occur, seven after reopening;
four later vendor repetitions have unexplained triggers. One final acknowledged
Auto pair correlates with restoration. No fan-demand writes or fan-state queries
occur. All 216 complete bodies pass checksums; shutdown and service restart
completed without errors. Two zero-AppEngine snapshots retain running services.

After capture, an opt-in metadata check initialized 91-byte memory buffers with
`HidP_InitializeReportForID` for candidates `00` and `05`. Both returned
`C0110010` (report does not exist). The collection has zero feature button/value
capabilities; this parser result leaves user-mode framing unresolved.
No feature report was sent. The enhanced inventory records raw parser statuses
separately from actual device I/O validation.

A fourth attended session reconnected USB while retaining 12 V power, with
pad-dependent HyperBoost inactive. The operator reported nothing abnormal and
confirmed the requested preferred-state restoration. Actual enumeration binds
the exact revision-0200 pad to MI_00 descriptor request/response frames 247/248.
The response is the complete 22-byte report descriptor, independently hashed
and decoded. It declares one unnumbered constant Feature item: size 8 bits,
count 90. There is no Report ID item. Windows' inventoried 91-byte buffer thus
requires prefix zero; historical prefix `05` is not declared. This resolves
framing choice; actual OpenBlade Windows feature I/O was unvalidated at that stage.
Capture stopped gracefully, OpenBlade restarted, and raw root traffic remains
private. BIOS versions are evidence provenance and do not gate validation.

The bounded vendor-baseline procedure is:

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

The single `00/84:0000` query in
[draft PR #624](https://github.com/OpenBladeProject/openblade-core/pull/624)
has completed successfully on build `37011fd6`. The operator exited Synapse and
explicitly authorized stopping only Razer Elevation Service and Razer Game
Manager Service 3. Fresh exact-device and controller checks passed with USBPcap
inactive. One Windows prefix-zero exchange returned raw `0000`, validated by
the shared session, without a setter, fallback ID or retry. The operator
reported normal unchanged behavior. At the user's explicit request, Synapse
and both services remain stopped. BIOS versions are provenance only.

This establishes actual Windows feature I/O for the observed raw query,
without laptop Driver/Normal semantics, fan-state, RPM or ownership claims.
The captured mode-family round trip has now completed in draft PR #624, from
reviewed build `746586c9`: fresh raw `0000`, set/echo `0300`, query `0300`,
immediate set/echo `0000`, final query `0000`. The operator approved this exact
trial and confirmed normal unchanged fan and full-brightness static-green
lighting afterward, without red/sleep indication. No retry, fan/lighting setter
or vendor restart occurred. Evidence is the hashed typed-result annotation in
captures PR #33, without concurrent PCAP or retained native response traces.

This validates immediate restoration of the saved raw value on this exact unit.
It does not establish meaningful fan state, ownership or safe fan handback. The
next fan-write work still needs a complete accessory acquisition/restoration
contract and bounded attended plan. The historical standalone Auto failure
continues to block presumed release.

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
