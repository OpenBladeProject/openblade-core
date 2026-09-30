# Cooling-pad continuous-control evidence gap

Native production admission remains closed. The finite service-curve result
supports acknowledged demand changes and attended restoration, not continuous
transaction reuse or session renewal.

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

## One bounded next capture

Prepare one exact-target vendor-controller session spanning an acknowledged
fan-control counter boundary, with predecessor and successful successors.
Do not start it under the current instruction to leave Synapse and its services
stopped. This plan records no new capture or hardware result.

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
   wrapper manages OpenBlade around capture. No OpenBlade HID query or control
   process may run concurrently with USBPcap on the interface.
4. Record the actual vendor UI selection and timestamped workload markers.
   Do not assume that the label Auto selects the host-managed Smart Curve.
   Establish that the chosen session emits changed `0D/01` demands; an ordinary
   windowed 3DMark run is a workload, not independent proof of those demands.
   Keep the same controller session and connection while collecting the boundary.
5. End the workload by minute four, reserving the final minute for restoration.
   While capture is still active, restore the saved vendor settings, check
   physical fan/lighting behavior and record the restoration marker. Then stop
   through the wrapper sentinel and verify graceful exit; the wrapper restores
   its saved OpenBlade state on exit. Leave Synapse/Razer services stopped
   afterward. If capture times out first, record later restoration as an operator
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

## Eventual implementation boundary

An eventual pad-specific ID policy must come from that evidence. Keep one
serialized exchange and validate its full response before permitting evidenced
reuse. Ambiguous submission, timeout, unexpected response or incomplete disposal
ends the epoch without retry or automatic renewal. A journal or saved request can
block admission but cannot authorize recovery, reattachment or firmware handback.
