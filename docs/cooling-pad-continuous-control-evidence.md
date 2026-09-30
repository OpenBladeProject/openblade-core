# Cooling-pad continuous-control evidence and remaining gaps

Native production admission remains closed. The finite service-curve result
supports acknowledged demand changes and attended restoration, not continuous
transaction reuse or session renewal.

The later ten-minute vendor trace below establishes acknowledged `0D/01`
demands across `1E -> 00`, followed by successful `01`, `02` and further demands.
It does not show an identical fan request ID twice within the recording,
independently instrument allocator lifetime, or validate native OpenBlade reuse.
Production admission remains closed. Its final selection exceeded the stimulus
deadline, recording timed out gracefully, and physical confirmation followed
timeout; successful cleanup does not rewrite those outcomes.

The subsequently authorized five-minute vendor capture is recorded in
[the bounded result](cooling-pad-vendor-continuous-capture.md). It contains one
acknowledged configured-2200 demand and no changed-demand successors; fan IDs
remain below `20`. The counter-reuse objective remains unresolved. Synapse and
Razer services were stopped afterward and OpenBlade was restored. The saved
Balanced CPU curve selects 2200 across rounded 40..99.9 degrees C, supporting an
unchanged-target explanation. Current records do not prove capture-time settings.
The later separately authorized Quiet/Balanced capture below contains three
acknowledged demands (2200 -> 1000 -> 2200) and complete captured vendor
restoration, but no fan-ID reuse. No repeat is implied by either completed run
or the preparation plan below.
Another separately approved repeated-preset capture observed acknowledged
`1E -> 00` during vendor Fixed/off restoration. Its seven demand ACKs remain
below that transition; no fan ID repeats within the trace and no fan commands
follow `00`. The demand-boundary objective therefore remains incomplete.

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
During the initial preparation review, the launcher was parsed but never
executed; no UAC, capture, service change or hardware operation occurred.
Successful cleanup is
recorded separately from capture completion and cannot rewrite `TimedOut`.

## Stopped setup and wrapper compatibility

The subsequently approved Quiet/Balanced attempt reached preparation but stopped
before recording on 2026-09-30. The private launcher enabled strict mode, which
the unchanged generic wrapper inherited. Its single-address expression unrolled
to a scalar; reading `.Count` threw before recorder state creation or native
recorder start. No recorder PCAP/ready/state/ownership file was created and no
preset selection occurred. The earlier 23 preparation checks did not exercise this
real-wrapper integration boundary.

The original launcher retained its failure and expired its cleanup gate because
it could not establish readiness. The operator separately confirmed unchanged
quiet/green behavior, no red/sleep and inactive HyperBoost, then fully exited
Synapse. A reviewed cleanup-only attended elevation verified controller absence,
stopped the two temporary Razer services and restored OpenBlade. Read-only IPC59
verified installed source `55718fa0` with pad Stop/Stopped/ObservedOnly. This
reconciliation does not rewrite the failed launcher result or claim captured
restoration. No OpenBlade pad Feature-report query/control command ran.

The future private launcher now invokes the generic wrapper in a child scope
with strict mode off, retaining strict mode in the launcher. Three additional
Windows PowerShell checks invoke the real wrapper with inert fake recorder
helpers: the original pre-start failure reproduces; the fixed call writes ready
and graceful terminal state; parent strict mode remains enabled. These and the
23 preparation checks passed independent review. The generic capture tooling is
unchanged. This establishes compatibility in the fake integration, not a live
capture result.

The repaired setup's next UAC request was canceled before its launcher started.
No new service change, recorder or preset action followed that cancellation.
The operator subsequently requested reopening; the new elevation is a separate
attempt, not an automatic privilege retry.

## Completed repaired capture and cleanup

The separately reopened attempt on 2026-09-30 recorded from 21:21:40 to
21:25:23 UTC and stopped gracefully through the explicit sentinel, before its
300-second limit. The unchanged wrapper ran successfully through the reviewed
compatibility scope. Recorder state is `Completed`, with no errors; the elevated
launcher exited zero and separately recorded completed cleanup.

The observed baseline was Fan Control off with retained Fixed/Medium 2200 RPM;
the saved underlying Smart preset was Balanced with CPU input. The operator
confirmed quiet normal operation, full-brightness static-green lighting, no
red/sleep indication and inactive HyperBoost. Recording covered enabling Fan
Control, selecting saved Smart Balanced, selecting Quiet (displayed demand 1000),
and selecting Balanced (later displayed demand 2200). Quiet's physical check
passed. The Balanced reply arrived after the stimulus deadline and did not
release more changes. Input-detected guards required fresh observations after
operator replies. Only three Smart selections occurred; the planned alternation
was not completed and cannot establish counter reuse.

Restoration began at the three-minute stimulus deadline: retained Fixed/Medium
2200 and Fan Control off were restored, with Smart Balanced retained. The final
operator check confirmed preferred quiet behavior, unchanged green lighting,
no red/sleep and inactive HyperBoost before the stop sentinel. This physical
confirmation is distinct from packet evidence of restoration commands.

After a separate full tray-exit confirmation, AppEngine and recorder absence
were verified. The same elevated helper stopped both temporary Razer services
and restored OpenBlade. Read-only IPC59 reports installed source `55718fa0`,
verified device identity and pad Stop/Stopped/ObservedOnly with no planned or
historical demand. No OpenBlade pad Feature query/control command ran, and no
software installation or version change occurred.

The private PCAP is 11,363 bytes, SHA-256
`DD872896CB33A642CF7271DF05C912EC16B48953A1C6F92CC71C491884CDB915`.
It contains 124 raw frames, 71 decoded payload entries and 59 complete 90-byte
bodies, all checksum-valid without truncation. Seven fan pairs have exact
transaction/command/length/payload echoes and successful status `02`:

| Request/response frames | Transaction | Command/payload | Configured demand or selection |
| --- | --- | --- | --- |
| 15/18 | `15` | `0D/10:01012C` | Fixed Medium 2200 |
| 23/26 | `16` | `0D/01:01052C` | Demand 2200 |
| 31/34 | `17` | `0D/01:010514` | Demand 1000 |
| 57/60 | `18` | `0D/01:01052C` | Demand 2200 |
| 97/100 | `19` | `0D/10:01012C` | Fixed Medium 2200 |
| 101/104 | `1A` | `0D/10:01012C` | Fixed Medium 2200 |
| 107/110 | `1B` | `0D/10:000600` | Vendor Fan Control off |

There are three acknowledged demands and two changed-demand successors, with
no unmatched or ambiguous fan bodies. Fan IDs `15..1B` are unique; no fan reuse
occurred. Forty-five unpaired lighting requests include `1F -> 00 -> 01` at
frames 53/55/61, which cannot establish fan-control counter policy.
The final off ACK at 21:24:50.532101 UTC precedes the physical restoration marker
at 21:25:22.939145 UTC and graceful recorder completion at 21:25:23.905718 UTC.
Thus vendor Fixed/off restoration and its subsequent physical check were within
recording. This does not validate an OpenBlade standalone off/handback command,
measured RPM, thermal causality or firmware Auto ownership.

The result supports distinct host-managed Smart demands, but the acknowledged
counter-boundary objective remains unresolved. Native admission and transaction
reuse remain closed. No further recording is authorized by this completed attempt.

## Repeated-preset capture: observed restoration boundary

The operator separately approved another 300-second recording with at most
35 selections, a 180-second stimulus limit and only preflight/final physical
reply gates while watching throughout. The repeated stimulus was restricted to
the captured CPU Quiet1000/Balanced2200 pair and rounded `50 <= T < 85` degrees C.
UAC, baseline/lighting inspection and the initial physical check preceded
recording; no preset mutation occurred during preparation.

Recording ran from 21:39:59 to 21:43:59 UTC on 2026-09-30 and stopped gracefully
through the sentinel with no errors. Seven Smart selections completed. The
initial Smart expansion and an offscreen CPU-selector click rejected without
input required fresh observation and scrolling; CPU was then visibly selected.
Root/UI observation latency also limited the sequence. The last stimulus was
at 171.551 seconds; the first restoration refresh was at 180.6 seconds, and
the off-restoration refresh at 191.422 seconds. These timings are retained,
without claiming that all 35 selections or restoration by 180 seconds completed.
No intermediate physical reply was used to release further actions.

The private PCAP is 13,409 bytes, SHA-256
`D0FC39E5C88562BB2612EAA610D2F71CF63E8D93547CD568828B0515AA2216C9`.
It contains 146 raw frames and 70 complete 90-byte bodies, all checksum-valid.
Eleven fan pairs have exact echoes and successful status `02`:

| Request/response frames | Transaction | Command/payload |
| --- | --- | --- |
| 13/16 | `15` | `0D/10:01012C` |
| 21/24 | `16` | `0D/01:01052C` |
| 53/56 | `17` | `0D/01:010514` |
| 63/66 | `18` | `0D/01:01052C` |
| 79/82 | `19` | `0D/01:010514` |
| 87/90 | `1A` | `0D/01:01052C` |
| 95/98 | `1B` | `0D/01:010514` |
| 105/108 | `1C` | `0D/01:01052C` |
| 111/114 | `1D` | `0D/10:01012C` |
| 115/118 | `1E` | `0D/10:01012C` |
| 125/128 | `00` | `0D/10:000600` |

Seven demand ACKs establish six changed-demand successors, with no unmatched
or ambiguous fan reports. The observed `1E -> 00` is across vendor Fixed/off
restoration, not `0D/01` demand updates. No fan ID value repeats within the trace
and no fan commands follow `00`. This supports the source allocator's boundary
candidate, but does not establish uninterrupted demand reuse or successors after
the boundary. Forty-eight lighting requests are unpaired; their independent
`1F -> 00 -> 01` at frames 51/57/59 does not change that conclusion.

Vendor off ACK at 21:43:10.500213 UTC precedes the final physical restoration
marker at 21:43:58.987665 UTC and graceful completion at 21:43:59.879648 UTC.
The operator confirmed preferred quiet/green behavior, no red/sleep and inactive
HyperBoost, then separately confirmed full tray exit. Controller/recorder absence
was verified before the same elevation completed cleanup with exit zero.
OpenBlade is running and both temporary Razer services are stopped. Read-only
IPC59 verifies unchanged installed source `55718fa0` and pad
Stop/Stopped/ObservedOnly, with no planned or historical demand. No OpenBlade pad
Feature query/control command, measured-RPM test or software install occurred.
Native admission, standalone handback and continuous demand reuse remain closed.
Further recording requires separate authorization; this attempt cannot extend
or restart automatically.

## Ten-minute capture: demand boundary and successors

The operator separately approved one fresh 600-second vendor recording, at most
35 existing CPU Quiet1000/Balanced2200 selections, a 480-second stimulus deadline
and 120 seconds reserved for restoration. A distinct scope acknowledgement
prevented using the old 300-second target. The minimally changed private launcher
and unchanged cleanup flow passed four scope checks, the original 23 preparation
checks and three inert real-wrapper checks under Windows PowerShell, with
independent review. These establish software preparation, not live endurance.
The 480-second/35-selection limits were external operator instructions.

UAC/setup, baseline and lighting inspection, and the operator's physical preflight
preceded recording. No preset mutation occurred during preparation. The saved
baseline was Fan Control off, retained Fixed/Medium 2200, Smart Balanced with CPU
input and full-brightness static green. After recording began, a user-input guard
blocked the initial click without submission; fresh observation and activation
preceded the actual input. CPU selection was confirmed by a fresh screenshot
after scrolling; no offscreen selector click was attempted in this run.

Twenty-seven Smart selections displayed alternating 2200/1000 demands, with CPU
readings in the scoped range. Fresh observations were saved after each change;
they are UI demand context, not measured RPM or packet ACKs. The final Balanced
selection at 22:06:39.443 UTC was approximately 483.709 seconds after readiness,
and its observation at 485.0 seconds. Both exceeded the 480-second stimulus
deadline. Fixed and off-restoration refreshes followed at 514.4 and 530.5 seconds.
This overrun is preserved; no complete compliance with the approved stimulus
deadline is claimed. Further stimulus did not occur.

The recorder reached its 600-second bound and stopped gracefully at
22:08:36.474815 UTC on 2026-09-30, with status `TimedOut` and no recorder errors.
The operator confirmed restored quiet/green behavior, no red/sleep and inactive
HyperBoost; the physical marker was saved at 22:08:40.427887 UTC, approximately
3.953 seconds after recorder termination. The subsequently created stop sentinel
did not cause that stop. This remains post-capture physical observation.

The private PCAP is 34,241 bytes, SHA-256
`EB69F85D6EC57512E7C809885F1FD10D207F0438CD58099CFD0EA89033731C96`.
It contains 370 raw frames, 218 decoded payload entries and 182 complete
90-byte bodies, all checksum-valid without truncation. All 31 fan pairs have
exact transaction/command/length/payload echoes and successful status `02`,
with no unmatched or ambiguous fan reports. Twenty-seven `0D/01` demand ACKs
establish 26 changed successors, alternating 2200/1000 and ending at 2200.
The demand boundary and immediate successors are:

| Request/response frames | Transaction | Command/payload | Configured demand |
| --- | --- | --- | --- |
| 111/114 | `1D` | `0D/01:010514` | 1000 |
| 117/120 | `1E` | `0D/01:01052C` | 2200 |
| 127/130 | `00` | `0D/01:010514` | 1000 |
| 135/138 | `01` | `0D/01:01052C` | 2200 |
| 143/146 | `02` | `0D/01:010514` | 1000 |

Further demand ACKs continue through `11`: seventeen demand successors follow
`00`. The complete fan request sequence is `15..1E, 00..14`, each value once.
It supports the source candidate's `1E -> 00` boundary and continued vendor
demand acceptance. No identical fan request ID repeats within this trace, and
no independent allocator-object instrumentation excludes an internal reset.
It is not proof of indefinite control, native Windows transport reuse or
cross-epoch/reacquisition safety. Lighting has 120 unpaired requests and four
independent wraps; they do not establish fan policy or lighting admission.

Vendor Fixed restoration is acknowledged at frames 325/328 and 329/332;
off `000600` at 339/342 is acknowledged at 22:07:24.927978 UTC, before timeout.
Captured restoration commands and the subsequent physical observation are
distinct; neither admits an OpenBlade standalone off/handback command.
After separate full tray-exit confirmation, controller/recorder absence was
verified and the same elevated helper completed cleanup with exit zero.
`cleanupCompleted=true` and `captureCompleted=false` preserve the graceful
timeout and its error. OpenBlade is running; both temporary Razer services are
stopped. Read-only IPC59 verifies unchanged installed `55718fa0`, verified host
identity and pad Stop/Stopped/ObservedOnly with no planned/historical demand.
No OpenBlade pad Feature query/control command or software install occurred.
Raw packets and vendor source remain private; no further recording is authorized
by this completed attempt.

## Eventual implementation boundary

An eventual pad-specific ID policy must come from that evidence. Keep one
serialized exchange and validate its full response before permitting evidenced
reuse. Ambiguous submission, timeout, unexpected response or incomplete disposal
ends the epoch without retry or automatic renewal. A journal or saved request can
block admission but cannot authorize recovery, reattachment or firmware handback.
