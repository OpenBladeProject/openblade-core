# Cooling-pad continuous-control evidence gap

Native production admission remains closed. The finite service-curve result
supports acknowledged demand changes and attended restoration, not continuous
transaction reuse or session renewal.

The subsequently authorized five-minute vendor capture is recorded in
[the bounded result](cooling-pad-vendor-continuous-capture.md). It contains one
acknowledged configured-2200 demand and no changed-demand successors; fan IDs
remain below `20`. The counter-reuse objective remains unresolved. Synapse and
Razer services were stopped afterward and OpenBlade was restored. The saved
Balanced CPU curve selects 2200 across rounded 40..99.9 degrees C, supporting an
unchanged-target explanation. Current records do not prove capture-time settings.
Review the specific stimulus before proposing another run; no repeat is implied
by the preparation plan below.

## What the current evidence supports

`ValidationRunContext` allocates IDs `01..FF` without reuse. This is a software
limit, not an evidenced cooling-pad counter width. The source-inferred highest
allocated ID in the successful `37b51016` trial was `17` hex: four restoration
reservations, six startup exchanges and thirteen subsequent demand changes.
No independent packet trace was collected for that trial.

The reviewed exact-target vendor Auto trace contains acknowledged controls
`1B` and `1C`, with no `0D/01` demand writes. The reviewed Fixed-exit trace
contains 38 non-lighting requests whose IDs remain below `20`. That private
trace is identified by SHA-256
`0A025B0B247D4397770C278C79A19AED26E0ECC838FD7F9F4327106B523B9932`.
Its unacknowledged lighting `0F/03` traffic cycles `1F -> 00 -> 01` at frames
243, 245 and 247. Lighting's policy cannot establish the acknowledged fan-control
counter policy. Orderly handle-replacement results likewise do not prove counter
exhaustion or uninterrupted host-demand renewal.

Do not wrap IDs, widen admission to guessed high-bit IDs, reopen as a counter
reset, or reacquire raw0300 on the strength of these observations. Raw control
state is not fan-state or firmware-ownership readback.

## Preparation for a separately authorized capture

Prepare one exact-target vendor-controller session spanning an acknowledged
fan-control counter boundary, with predecessor and successful successors.
The first bounded capture was inconclusive. Further vendor sessions need
separate authorization under the restored instruction to leave Synapse and its
services stopped.

1. Obtain separate authorization for the attended vendor session. Save the actual
   fan mode, preset/curve settings, lighting and OpenBlade process/service state.
   Confirm normal operation, unchanged full-brightness static green and inactive
   HyperBoost. Keep USB and 12 V connected throughout.
2. Reverify `RZ09-05289EN4`, pad `1532:0F43` revision `0200`, MI00 and report geometry.
   Use `captures/tools/Get-CoolingPadCaptureInventory.ps1` to reverify the USBPcap
   root and device address immediately before capture. BIOS is provenance only.
3. Use the generic `captures/tools/Invoke-InteractiveUsbPcapCapture.ps1` in the
   existing attended administrator workflow, restricted to that verified pad
   address and a new private directory, with a five-minute capture limit. The
   attended launcher manages OpenBlade around capture; the generic wrapper uses
   `-SkipServiceManagement`. No OpenBlade HID query or control process may run
   concurrently with USBPcap on the interface.
4. Record the actual vendor UI selection and timestamped stimulus markers.
   Do not assume that the label Auto selects the host-managed Smart Curve.
   Establish that the chosen session emits changed `0D/01` demands; an ordinary
   workload is not independent proof of those demands.
   Keep the same controller session and connection while collecting the boundary.
5. End the stimulus by minute three, reserving two minutes for restoration.
   While capture is still active, restore the saved vendor settings, check
   physical fan/lighting behavior and record the restoration marker. Then stop
   through the wrapper sentinel and verify graceful exit. The attended launcher's
   separate cleanup gate restores its saved OpenBlade state only after verified
   controller absence. Leave Synapse/Razer services stopped afterward. If capture
   times out first, record later restoration as an operator
   observation, not captured protocol evidence. Stop the investigation on
   uncertainty; do not substitute OpenBlade recovery commands.

If the bounded interval does not contain a correlated acknowledged fan-control
boundary, retain it as inconclusive. A high USB packet count or lighting rollover
alone is insufficient. Do not automatically extend, restart or alter the session.

Decode only the pad traffic. Separate acknowledged control exchanges from
unacknowledged lighting, correlate transaction/command/length/checksum/status and
exact semantic payload, and identify session continuity and restoration evidence.
Preserve raw traffic, serials, instance IDs, paths and unrelated USB payloads
privately. Publish only reviewed sanitized observations, validated with the existing
capture-evidence tools.

## Concrete demand stimulus

The existing saved Smart presets provide a simpler candidate than another
benchmark. Current CPU Quiet points are `(40,500), (50,500), (85,1000),
(90,1600), (100,1600)`; Balanced points are `(40,1000), (90,2200), (100,2200)`.
Read-only cached-source inspection verifies Quiet/Balanced enum mapping and
strictly-greater step selection. At rounded CPU 45, 60 and 85.6 degrees C,
Quiet selects 500, 1000 and 1600 respectively; Balanced selects 2200 at all three.
These saved curves yield distinct nonzero targets within the evaluated 40..85.6
range. Current records have no record-level timestamps and are preparation,
not proof of the earlier capture's runtime settings.

Subject to separate authorization and fresh UI/settings verification, select CPU
Smart control and alternate the existing Quiet/Balanced presets within one
retained vendor-controller session. Each selection must persist long enough for
the ordinary monitor callback; no guaranteed write cadence follows from clicking
a preset. Do not edit curves or start a workload. Bound the attempt to at most
35 selections and end the stimulus by 180 seconds, even if the objective remains
unmet. Restore the complete saved configuration while recording is still active,
then obtain the physical check and request graceful capture stop. Count actual
successful sequential logical demand exchanges, separating vendor retries and
interleaved commands; clicks are not ACK evidence.

The inspected shared middleware allocator is a source candidate: it emits hex
`00..1E`, then `00`, with `1F` skipped. Its concrete pad binding and firmware
acceptance were not established. Thirty-five selections are a planning ceiling,
not a guarantee of a counter boundary. Decode the actual chronological trace;
do not force a presumed `1F -> 00` transition or borrow the lighting policy.
The desired result is an acknowledged `0D/01` predecessor, observed reuse and
at least two successful successors in the same session, with complete captured
restoration. An inconclusive or ambiguous result must remain so, without retry.

Alternating the existing Fixed Low/Medium presets (saved 1600/2200) is a separate
boundary-only alternative. Its `0D/10` exchanges would not establish `0D/01`
demand continuity and cannot replace that objective. No preset selection, next
capture, controller restart or production ID-policy change is authorized by this
preparation.

## Cleanup preparation after the timeout

The recorded session's recorder stopped gracefully at 300 seconds, then the
wrapper threw its timeout exception. The private launcher's outer exception
handler therefore skipped the separate cleanup gate. This is an orchestration
failure, not evidence that recorder ownership or pad state changed.

Before another authorized session, put the recorder invocation behind its own
exception boundary. Preserve its terminal timeout/failure result and enter a
separate bounded cleanup gate under the same attended elevated launcher. A
ten-minute cleanup window accepts explicit operator readiness only after
saved-setting restoration and normal physical behavior are confirmed. The marker
must match this session's nonce and follow this recorder's readiness timestamp;
pre-capture confirmations cannot release cleanup. Recheck the monotonic deadline
after observation and recording work, immediately before cleanup dispatch. Verify
AppEngine and the owned recorder are absent before stopping the temporarily
authorized Razer services and restoring OpenBlade's saved running state.
AppEngine still being present keeps cleanup blocked within that window; it must
not terminate the gate immediately or cause a forced exit of the controller.

Unknown recorder ownership, failed graceful shutdown or cleanup-window expiry
must retain an unresolved record and leave conflicting service restoration
blocked. No pad recovery command, forced controller termination, automatic
capture retry or extension is authorized. Restoration completed after recording
ends remains operator observation. A private launcher and pure orchestration
helper implementing this preparation passed 23 hardware-free checks under
Windows PowerShell. They cover timeout-to-cleanup flow, fresh confirmation,
controller absence, ownership uncertainty, deadline expiry and no second cleanup
attempt after failure. The launcher also rechecks root/address, the same exact
pad instance/revision and competing-controller absence after the capture-start
gate, preserving separate initial/start inventories. A mismatch starts no
recorder and leaves cleanup behind the explicit physical gate.
The launcher was parsed but never executed; no UAC,
capture, service change or hardware operation occurred. Successful cleanup is
recorded separately from capture completion and cannot rewrite `TimedOut`.

## Eventual implementation boundary

An eventual pad-specific ID policy must come from that evidence. Keep one
serialized exchange and validate its full response before permitting evidenced
reuse. Ambiguous submission, timeout, unexpected response or incomplete disposal
ends the epoch without retry or automatic renewal. A journal or saved request can
block admission but cannot authorize recovery, reattachment or firmware handback.
