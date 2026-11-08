# SO6120 Stop/Auto guardian integration

The service Stop/Auto request now delegates an activation to a separate native
owner. It does not run Capture or change the installed driver. This is an
experimental implementation on exact Blade 16 RZ09-0581 / `1532:02E0`, using
the reviewed SO6120 EC 1.04 command contract. Other Blade models do not inherit
its registers, driver, admission or physical results.

Entry remains scoped to Balanced on USB-C. The initial destination is zero
PWM directly through the reviewed coordinator; the half-cycle step in the
attended experiment is not a firmware prerequisite. Stop is activation-local
and is never restored from a saved profile after startup.

The formerly selected Windows TZRZ thermal counters are rejected because their
firmware methods return a constant 45.05 C. CPU readings now use the bundled
LibreHardwareMonitor CPU subset through PawnIO, in a separate job-owned helper
with a one-second request deadline. Missing or failed readings refuse Stop entry
and end an owned Stop immediately. Installed service observations now confirm
changing CPU readings on RZ09-0581. The installed guardian path remains under
validation; earlier Intel diagnostics and EC fan experiments are separate
results. See [CPU temperature source](so6120-cpu-temperature-source.md).

Each new guardian prepares the CPU helper under a separate ten-second startup
deadline after host/session admission and before opening the fan-control
interface. It then takes a fresh safety reading with the ordinary sample-age
limit. Readiness alone never authorizes Stop, and canceled/failed preparation
cannot submit a fan command.

## Duration and transitions

There is no fixed Stop dwell timer. Selecting Auto, making an explicit
performance/profile/fan-mode change, losing the active session, or failing
continuation safety requests Auto from the same owner. The current conservative
Auto thermal policy requires an admitted CPU-temperature reading between 10 and 55 C, no
active GPU, and a reading completed within two seconds. Custom uses separate
CPU/GPU limits (55 C by default) and independent time-weighted rolling
averages (five-second windows by default). Each average covers a full trailing
window; a brief cool reading contributes to that average instead of resetting
a timer. Either average exceeding its limit requests Auto. Separate editable
CPU/GPU immediate cutoffs (65 C by default) request Auto on a single fresh
reading above the cutoff, without waiting for the window. A cutoff must be at
least its average limit; equal values give the immediate cutoff priority.

Custom Stop entry requires a fresh instantaneous reading at or below each
configured immediate cutoff. Average limits apply to continuation after a full
window is available; entry reads do not contribute to that window. Auto retains
its CPU 55 C and inactive-GPU entry policy. GPU activity alone
does not end Custom Stop. Unavailable, stale or implausible readings still end
continuation immediately. Each average weights the previous valid observation
by its actual elapsed duration; rapid sampling cannot outweigh a longer
interval. Confirmed GPU D3 clears only the GPU window. On wake, the GPU starts
a fresh full window while its immediate cutoff remains active.

Thermal exit diagnostics retain the triggering CPU or GPU reading, the limit
and whether an immediate cutoff or rolling average caused Auto. Average triggers
include their covered window. The decision is recorded from the sample used by
the guardian and survives coast-down and Auto cleanup; later service telemetry
does not replace it. Entry refusals record their own instantaneous comparison.

The Stop card has independently foldable End Stop and Automatic Stop sections.
Custom warns for average limits above 60 C or an averaging window above ten seconds. Input bounds are
10-100 C and 1-300 seconds; they are parsing bounds, not independently validated
safe operating limits. An active guardian keeps its immutable policy. Changing
that thermal policy requests and confirms Auto before saving the next policy.

Automatic Stop is off by default. When enabled, both available CPU and GPU
temperatures must stay strictly below their individually configurable limits
(50 C by default) for a complete cool interval (30 seconds, configurable from
1 to 3600). Reaching either limit or a missing, invalid or slow read-only sample
resets the interval. Temporary admission failures wait for fresh evidence;
they do not silently switch off the user's waiting intent.
The start limits must be below the corresponding Custom exit limits; Auto
keeps its existing 55 C CPU/inactive-GPU entry policy. A long cool interval
does not receive the warning used for delaying fan restart.

Automatic entry uses the existing service monitoring loop and the same fresh
exact-device, controller, power, session and guardian admission as manual Stop.
It does not widen the USB-C Balanced entry scope. Only a successful thermal
Auto completion, including cleanup, journal completion, native close and
original-owner exit, permits another full cool interval. Manual Auto, explicit
mode changes, power increases, session/lifecycle changes and ownership or command
faults pause re-entry until Resume. Lower-power transitions retain waiting intent
as well as the existing owned-Stop behavior. Only periodic maintenance can start
an automatic Stop; guardian notifications reconcile the existing operation.
Settings persist per user; a new service/user context starts paused. Resume
begins observation and cannot immediately issue Stop. An uncertain or refused
attempt is not automatically retried. Arming and pause generations change
atomically, so Resume cannot overwrite a concurrent lifecycle or manual pause.
Each sample keeps its two-second acquisition bound. Continuity checks measure
the uncovered interval before the next read, without counting that read's
bounded acquisition time twice. A genuinely missing interval resets unowned
entry waiting and ends owned Custom Stop. Contended telemetry can delay entry;
missing time never counts toward the cool interval.

Custom and automatic-entry sampling distinguish workload inactivity from
power state. The optional source reads Windows' latest reported
`CM_POWER_DATA.PD_MostRecentPowerState` for one present NVIDIA display adapter.
D3 permits an absent GPU temperature; D0 requires a successful temperature read
and a second D0 observation. D1/D2, unknown state, multiple adapters and malformed
data fail the sample. The source does not invoke the GPU API for D3/unknown,
and releases a Stop-only temperature session after each D0 read. It may borrow
an ordinary active reader's existing session without renewing its idle deadline.
This avoids repeated driver initialization while preserving fresh command
observations rather than hiding them behind a shared cache. This is OS-reported
state, not proof of D3cold or physically validated no-wake behavior. Ordinary
fan-curve and Auto-policy sampling retain their existing behavior.

Policy settings use configuration schema 25 and service IPC 77. Schema-24
settings retain their mode, temperature limits and duration; the duration now
controls the averaging window. Each new immediate cutoff starts 10 C above its
corresponding saved limit, capped at the existing 100 C input maximum. A saved
100 C limit therefore keeps an equal 100 C immediate cutoff. Older pre-Stop
settings still migrate with Auto selected and automatic Stop disabled. Each
installed trial below identifies the policy build it exercised.

External power is classified by the existing typed adapter query in the owner,
serialized outside mailbox transactions. Windows external-power indication by
itself cannot distinguish USB-C from the full-power adapter.

| Change while Stop is owned | Action |
| --- | --- |
| Same known source | Keep Stop while continuation admission holds |
| AC to USB-C, USB-C to battery, or AC to battery | Verify the existing owned controls and zero PWM, then keep Stop |
| Battery to USB-C/AC, or USB-C to AC | Return to Auto |
| Complete adapter reply with an unknown value | Request same-owner Auto |
| Unresolved native observation | Stop further commands and report recovery required |
| Explicit mode change | Complete Auto and release ownership before applying the destination |

The power comparison is generic; it does not widen initial entry admission.
Destination profile intent is retained during a downward power transition, but
its ordinary hardware writes wait for Auto. The owner never rewrites zero or FE
to fight a firmware reset. If the downgrade changes owned fields, the complete
control observation fences the operation instead of inventing ownership.

Same-power firmware changes made outside OpenBlade do not have an established
continuous notification channel. The ordinary thermal query cannot be assumed
to decode the direct FE selector. This implementation detects competing
controllers and checks owned controls before cleanup and on power downgrades;
it does not claim continuous detection of every external register change.

## Ownership and recovery

The service marks delegation pending, drains its shared hardware semaphore,
then retains that semaphore for the whole activation. Queued and new ordinary
HID reads and writes are rejected. The independent owner alone performs the
reviewed mailbox and adapter queries during this interval.

A hardware-free proxy creates the private breakaway/kill-on-close job used by
the attended guardian. The native owner escapes that private job. Independent
control readers observe both proxy and service heartbeats; either loss requests
Auto. The installed service is not assigned to a disposable per-activation job.
The private SYSTEM-only channel binds the original service, launched owner,
operation ID and increasing status revisions. It exposes Stop and Auto signals,
not raw registers or PWM values.

Before launching a child, the service flushes a create-once pending reservation
in protected machine data. Any old marker, malformed marker or read failure
blocks acquisition on restart without issuing a hardware command. A journal
cannot be replayed or adopted by a new service. The live service archives its
reservation only after independently observing the original owner's exit and
all required evidence: Auto readback, owned-field cleanup, complete journal and
native handle release. A clean refusal can release ownership without claiming
that Auto was observed only when explicit command evidence proves no EC mutation
and no pending or unresolved native command. Completed read-only commands do not
establish Stop ownership. Missing checkpoints alone never prove no write occurred.

The UI distinguishes preparing Stop, confirmed Stop, returning to Auto, and
recovery required. Historical confirmed Stop is retained even if the owner
returns to Auto before the next service status sample. Cancellation of the UI
request does not cancel mandatory native confirmation or authorize a replay.
Confirmed Stop additionally requires a complete zero-PWM, zero-tach observation
after bounded coast checks. Continued coasting requests Auto; ambiguous reads
remain fenced. This is EC-indicated stop, not an independent mechanical sensor.

Startup allows ten seconds for the first authenticated heartbeat, followed by
the normal five-second lease. The continuous monitor samples cheap session,
controller and thermal inputs once per second. Each native command still checks
fresh host, HID, controller, ownership and driver-service admission. Only the
boot-reported EC version and bytes of the already verified, write/delete-pinned
driver file are reused. Service heartbeats use an immutable status snapshot and
volatile lifecycle flags; they cannot issue HID reads or take the service gate.

Keyboard shutdown still attempts its guarded Normal-mode release first. An
attempt definitely blocked by guardian delegation may be retried only after
verified Auto and native release. An uncertain keyboard operation is not
replayed. Installation and removal must also preserve any active owner and
unfinished guardian records.

## Evidence and remaining validation

The shared coordinator, journal, port and caller-isolation code are extracted
into `OpenBlade.FanStop.Core`; Capture keeps its separate attended consent,
stopped-OpenBlade admission and developer attempt marker. The product owner
requires the already installed pinned InpOut driver. No driver or vendor DLL
is provisioned for EC access. CPU sensing separately bundles the reviewed
PawnIO installation described in the CPU source guide.
The product host check uses the exact model and HID identity; SKU and BIOS are
diagnostic metadata. The EC 1.04 requirement is explicit because this register
contract was decoded from that exact image. Its source SHA-256 is
`8537E9FD55B7B35504D8D87B0191ACC4391B67365B3FE72F6E1029A424C9E707`,
not a claimed hash of running EC memory.

The [attended caller-loss result](so6120-guardian-caller-loss-validation.md)
establishes recovery by the surviving owner for its frozen Capture build.
It does not validate the new installed Session-0 delegation path, direct zero
entry without the spinning test step, sustained Stop, or power transitions.
These still need one appropriately scoped, attended installed-path test after
review. The 2026-11-07 broker installation and changing CPU readings are
confirmed. Its first installed Stop requests were refused before any EC command:
one by the completed developer marker and one by initial command-free admission.
Both reported zero resolved EC commands, no mutation and no pending command.
The diagnostic-only `220097f9` build was then installed and all 372 payload files
verified. Its supervised request reached the guardian after fresh service CPU
samples of 50, 51 and 50 C. The guardian independently measured 62 C in 172.9 ms;
Auto requires at most 55 C. GPU inactivity passed. It refused before opening the
fan interface or publishing an EC command and returned released/inactive state.
Fans remained Automatic, with no blocked writes or unconfirmed submission.

This establishes the precise thermal refusal and operation of the guardian's
fresh CPU read. It does not establish successful installed Stop or Auto
restoration. That trial kept Auto's limits, and a matching ordinary Automatic
mode is not presented as restoration evidence.

A separately authorized Custom trial on the same installed build temporarily
changed only the CPU limit from 55 to 65 C. GPU remained 55 C, the continuous
exit delay remained five seconds, and automatic entry stayed disabled. Fresh
service samples of 52, 52 and 61 C admitted dispatch. The initial guardian check
passed, but its later cleanup-checkpoint admission returned `AdmissionRefused`
before any EC command (zero resolved commands, no mutation or pending command).
The specific later predicate was not exposed; the 72 C restoration sample does
not prove temperature caused that refusal. The client independently confirmed
released/inactive ownership, restored the exact original Custom 55/55 C and
five-second settings, and verified their readback. Fans remained Automatic at
2300/1900 RPM. No fan-stop or Auto-restoration success is claimed for this trial.
This trial preceded the rolling average installation described below.

Later entry checks now retain the failed stage and that attempt's available
sample in the refusal receipt. Each attempt clears earlier diagnostics; a
prerequisite failure before thermal acquisition is marked as not sampled.
This changes reporting only, without adding hardware reads or changing
admission and recovery ordering.

On 2026-11-07, the reviewed `e820aed2` rolling-average build was installed through
the enrolled broker as LocalDev `65535.417.48188.62986`. Publishing passed all
171 installer UI tests; independent verification matched all 372 installed
payload files and the running service, Session Agent and tray. The typed IPC-77
readback preserved Custom 55/55 C, the five-second window and disabled automatic
entry, with both new immediate cutoffs migrated to 65 C.

The authorized repeat changed only the CPU average limit to 65 C. GPU stayed at
55 C, both immediate cutoffs stayed at 65 C, and the window stayed five seconds.
After an initial 79 C sample, three fresh service samples of 59, 57 and 56 C
admitted the single Stop request. Saved status moved from Starting directly to
ReturningToAutomatic, without a confirmed Stopped snapshot. The receipt reported
"Auto requested or continuation admission ended" without identifying the cause.
One harness-issued Auto request completed with firmware Auto, durable cleanup
and native release confirmed; independent public readback also confirmed released
ownership. Every original setting was then restored and independently matched.
Final readings were CPU 58 C and fans 2500/2300 RPM, with no blocked or unconfirmed
writes. This validates the observed Auto cleanup and settings restoration, but
does not establish fan stop, a stopped dwell or operation of the rolling exit
thresholds. No further Stop request was sent.

Read-only inspection of that run's protected journal verified its 659-record
hash chain. The owner confirmed the override permission bit, then refused the
final FE command in the fresh entry check before any FE transfer began. All
snapshots retained PWM 58/52 and tach 25/23. Cleanup restored the permission bit
and completed the journal. This was partial entry, not a PWM convergence or
coast-down failure. The old build discarded the failed check's specific detail
when an owned cleanup checkpoint existed, so its precise predicate cannot be
recovered from this record.

The corrected owner retains the first entry/coast cause through successful Auto
cleanup. It records Stop as confirmed only after stopped tachometers are observed,
and keeps that history through later cleanup. The service reports a Stop request
as blocked when Stop was never confirmed, while preserving independent Auto
release proof. Failed Auto cleanup remains a failed result even after an earlier
confirmed Stop. No thermal threshold, freshness rule or hardware sequence changes
as part of this correction.

The corrected `8c869310` build was installed through the fixed broker as LocalDev
`65535.417.48217.64084`; all 372 installed files and the running component
generations matched. A separately authorized Custom CPU 65 C repeat preserved
GPU 55 C, the five-second averaging window and both 65 C immediate cutoffs.
After CPU preflight readings of 55/60/57 C, the owner reached coast-down but
returned Auto for `ThermalThreshold` before confirming stopped tachometers.
The Stop receipt correctly reported Blocked and retained that reason through
cleanup. The receipt does not identify the sensor or immediate-versus-average
branch, so neither is asserted as the specific trigger. Earlier preflight
samples ranged from 54 to 76 C; those are context, not the guardian's cutoff
sample. One Auto signal, independent released readback and exact original
settings restoration all completed. Final readings were CPU 61 C and fans
2500/2200 RPM, with no blocked or unconfirmed writes. This verifies the corrected
refusal reporting and restoration path, not successful fan stop or a stopped
dwell. This run's thermal refusal does not reconstruct the earlier run's missing
fresh-entry predicate.

The `fc54c108` diagnostic build was installed through the broker as LocalDev
`65535.417.48238.46106`, with all 372 installed files and running component
generations verified. Its authorized Custom CPU 65 C trial passed preflight at
54/57/54 C. The guardian's next fresh entry check recorded
`thermalRule=ImmediateCutoff; cpuC=73; cpuLimitC=65`, with a 164.5 ms sample age
and confirmed GPU D3. It refused before any EC command, retained the measured
reason in the receipt and subsequent status, and conclusively released the
unused owner. The fans remained Automatic; no Auto command was needed. Exact
original settings were restored and matched independently. Final readings were
CPU 52 C and fans 2200/2100 RPM, with no blocked or unconfirmed writes. This
confirms the new trigger diagnostics and settings restoration, not a stopped
interval or Auto restoration after a fan write.

An additional authorized trial kept the CPU average at 65 C and raised only its
immediate cutoff to 80 C, preserving the GPU limits and five-second window.
After 52/53/53 C preflight, the guardian recorded `thermalRule=EntryLimit;
cpuC=70; cpuLimitC=65` with a 178.5 ms sample age and GPU D3. The instantaneous
entry limit refused Stop before any EC command; the 80 C cutoff was not reached.
Fans remained Automatic, ownership conclusively released, and every original
setting, including the CPU cutoff, was restored with exact readback. Final CPU
was 51 C and fans 2200/1900 RPM. Raising the immediate cutoff does not widen the
then-current instantaneous entry limit; this trial did not confirm Stopped.

The subsequent requested change makes Custom entry use the configured CPU/GPU
immediate cutoffs, including equality, while preserving fresh valid readings
and explicit GPU power-state evidence. The separately configured Automatic Stop
cool threshold and dwell remain unchanged. Average-window history still begins
with owned continuation, so entry and command reads cannot credit elapsed time.

Guardian termination, an unknown outer-job termination, a native call that
never returns, sleep/resume and OS failure do not have a demonstrated
independent cooling fallback. A fenced owner retains its native resources;
neither the service nor Setup kills it to clear the condition. A previous
developer attempt also requires independent completed-run evidence before it
can be disregarded; the new service does not erase its marker.

## Completed developer attempt

The one-time developer marker from the reviewed caller-loss run is separate from
product reservations. `build/Archive-So6120CompletedDeveloperAttempt.ps1` supports
its explicit offline archival. Supply the local evidence directory; the default
only verifies the pinned complete journal, log, native-close and independent
process-exit receipts. It does not access the machine marker or hardware.

An explicitly requested `-Archive` run from an administrator console additionally
holds the global owner semaphore, rechecks original process absence or later PID
reuse, and renames only the matching marker without changing its bytes. Unknown
runs, altered evidence, active owners and existing destinations are refused.
The tool does not reopen a device, restore firmware or replay a journal. Adding
another run requires separate evidence review. This implementation was tested
using temporary marker fixtures. On 2026-11-07, the matching completed caller-loss
marker was archived with explicit user approval after independently verifying its
completion evidence. The original bytes were preserved; no EC command was sent.

See [review dispositions](so6120-stop-auto-guardian-review.md) for the independent
guardian review, and [Stop settings review](so6120-stop-settings-review.md) for
the configurable thermal policy, automatic-entry review and subsequent fixes.
