# SO6120 fan-stop crash and hang recovery

An ordinary unattended Stop switch is **not admitted by the current evidence**.
A separate surviving process can request the already reviewed Auto handback
after a caller disappears, but only while it still owns a resolved operation.
It cannot guarantee recovery from its own crash, unresolved native I/O, an OS
hang, or an unannounced low-power transition. A software heartbeat is not an
independent firmware fallback.

This is an offline design review against base
`d43a4e9af65683939d004a13bb238b0709bbc878`. No hardware access, driver load,
service change, installation, marker change, crash injection, or sleep test was
performed. The two completed attended USB-C Balanced trials already establish
the narrow `91/91 -> 0/0 -> Auto` sequence on exact RZ09-0581; the repeat also
has the owner's brief physical-stop confirmation. They establish neither a
crash recovery path nor an unlimited stop interval. See the
[attended evidence](so6120-supervised-fan-stop-trial.md#attended-result-on-2026-11-06).

## Failure coverage

| Failure | What can survive | Recovery actually supported |
|---|---|---|
| Tray crash or disconnected UI | A separately running service/guardian and its hardware session | The surviving owner can stop further experiment work, finish a started transaction and its mandatory confirmation, then use its last confirmed checkpoint for Auto. It must not depend on the dead UI to acknowledge cleanup. This design is feasible; the existing attended runner does not implement this product behavior. |
| Service crash while an independent guardian owns the direct session | Guardian, journal, handle and in-memory checkpoint, if its lifetime is genuinely independent | The same conditional cleanup is feasible. Service restart must remain fenced from thermal writes until the guardian reports and durably records handback and release. A helper killed with its parent does not supply this coverage. |
| Hardware-owner helper/guardian crash | EC state and durable journal may survive; in-memory operation is lost | Current journal inspection never authorizes resume. A future takeover requires a separately reviewed claim/reconciliation protocol. A clean journal tail alone does not establish current ownership or absence of an unrecorded acknowledgement. No current automatic Auto-on-restart path exists. |
| Live owner or native `DeviceIoControl` hangs | Another process can detect missing progress | Detection cannot establish that a command was unsubmitted, that it completed, or that the old native call has ended. No second writer, forced close, kill-and-retry, or alternative HID Auto is admitted. |
| Journal flush or admission collection hangs | A separate status observer may still respond | The owner cannot safely outrun a pending durable intent/confirmation or use stale admission. A heartbeat worker being alive does not prove transaction progress. No hard exposure bound follows. |
| Windows hangs or crashes | EC execution can continue | No recovered host-loss predicate guarantees exit from active FE. A user-mode guardian shares the failed host and cannot provide recovery. |
| Planned sleep, shutdown, or hibernate | A responsive owner may receive advance notification | Request Auto before the boundary and confirm it while the host is active. Notification timing is not a guaranteed veto or handback window. Never persist Stop for startup/resume reapplication. Unexpected transitions remain unvalidated. |
| EC enters its inactive/reset lifecycle | Firmware has selector-reset and zero-duty paths | Those paths are conditional on firmware state/events; they do not establish cooling during host failure or exact restoration after resume. Do not use forced sleep/reset as a recovery command. |

## Why the remaining recovery candidates do not suffice

The EC image was rehashed in this review:
`artifacts/silent-firmware-research/block-07-044de8.bin`, SHA-256
`8537E9FD55B7B35504D8D87B0191ACC4391B67365B3FE72F6E1029A424C9E707`.
The targeted instruction checks below reuse the prior
[writer census](so6120-ec-pwm-writers.md), rather than repeat it.

1. **Normal temperature feedback:** `158AE..158B6` branches around normal
   controller `15510` when selector is FE. The FE branch `16262..162C8` checks
   permission and copies staged bytes on mismatch; the examined branch has no
   temperature, lease, or liveness predicate. A rising temperature is not an
   established automatic escape from FE. Primary listing:
   `artifacts/silent-firmware-research/ec-disassembly.txt:20502-20504` and
   `21278-21309`.
2. **Permission fallback:** `162CA..162DC` can select the configured fallback
   only after bit 6 is clear. Host silence does not itself clear that bit in
   this path. Writing it as an emergency shortcut would be another hardware
   command, not independent recovery, and must not replace the reviewed
   selector-first restoration. Listing `21310-21316`.
3. **EC watchdog:** `C8D8..C8E0` writes `5C` to the watchdog from scheduled EC
   work before later state checks. Its feed is not a lease renewed by the host
   application. Reset branches previously traced in the writer census have
   separate predicates. Listing `9843-9853`. No watchdog programming or forced
   reset is proposed.
4. **Sleep/reset:** `148A2` selects fallback, then `148C0/148C4` clear both
   duties. `1495C` calls the selector reset and `14980/14984` also clear duty.
   These are lifecycle transitions, not high-duty cooling recovery. Their
   triggers do not prove that an awake but hung Windows host will reach them.
   Listing `19224-19243`, `19276-19293`; the event gates are documented in the
   prior census. Timing and resume behavior remain physically unvalidated.
5. **Handle close or driver unload:** the native wrapper calls `CloseHandle`
   only; session disposal explicitly sends no commands
   (`So6120WindowsDirectDutyPlatform.cs:72-82`,
   `So6120DirectDutySession.cs:302-312`). The reviewed driver's initialization
   installs CREATE and DEVICE_CONTROL handlers and an unload routine; the
   inspected setup installs no fan-restoring CLOSE/CLEANUP handler. Unload
   deletes the link/device, without a fan command. Primary dump:
   `artifacts/host-mailbox-updater-20261105/native/embedded64-memory-analysis.txt:541-553`
   and `30-52`, with dispatch instructions at `609-614`. The extracted driver
   was rehashed as
   `F8965FDCE668692C3785AFA3559159F9A18287BC0D53ABB21902895A8ECF221B`.
   This is static evidence about the pinned driver, not permission to unload it.
6. **A second Auto writer:** InpOut is nonexclusive and each index/data pair
   spans separate native calls. The EC mailbox has no owner or request ID.
   An old handler can clear a replacement command. The existing process
   semaphore serializes this developer tool only; it is not a device lease
   (`So6120WindowsDirectDutyPlatform.cs:251-261`). Neither acquiring a host
   lock nor observing a missing PID proves that a pending EC request resolved.
   The [mailbox contract](so6120-host-mailbox-contract.md#request-publication-and-ownership)
   records the exact driver and EC instructions. Ordinary HID Auto also has
   cache/pre-read hazards; it is not a fallback for an ambiguous direct-mailbox
   operation ([Auto analysis](so6120-direct-pwm-handback.md#auto-exit-and-the-installed-ui)).

These checks rule out those specific mechanisms as established recovery. They
do not prove the absence of every hardware protection outside the recovered
code or quantify the laptop's thermal tolerance with stopped fans.

## Existing implementation boundaries

The gated service controller requires an explicit, command-free startup
ownership verdict. Unknown or unresolved ownership blocks controls before
startup reconciliation. A future live composition must obtain that verdict
from independently verified durable or guardian state; the interface alone
does not implement persistence, takeover, or crash recovery. Its diagnostics
retain transition and recovery causes for the current service lifetime, not
as a substitute for a durable ownership journal.

The current byte adapter holds a lifetime lock and retains its SafeHandle
across `native.Invoke`. Its `DeviceIoControl` call is synchronous, with a null
overlapped pointer (`So6120InpOutBytePort.cs:69-105`, `118-134`). Dispose waits
for the same lock (`108-115`). Moving that invocation to a task or adding a
timer cannot establish native completion. Closing an uncertain handle or
killing its process is not an approved cancellation protocol.

The session serializes entry and restoration, requires an unused in-memory
checkpoint, and fences commands on uncertain transfer or storage results
(`So6120DirectDutySession.cs:109-149`, `196-252`). Its journal flushes each
record to disk before accepting the candidate state
(`So6120DirectDutyJournal.cs:122-159`). Inspection explicitly exposes
`CanResume => false` (`16-23`); every incomplete journal remains unresolved
(`163-208`). Valid bytes after a crash do not prove the old caller received
the flush acknowledgement. The persistent attempt marker also survives
failure (`So6120WindowsDirectDutyPlatform.cs:328-347`). Do not delete it to
make a restarted helper look like a fresh attempt.

The reviewed handback is direct: restore the captured normal selector, confirm
normal mode/manual state, then restore owned override/staging bytes. It does
not first return to half-cycle duty, clear the whole override byte, or select
a default performance profile
(`So6120DirectDutyCoordinator.cs:226-265`). The Auto phase already uses its own
budget and an uncancelled cleanup token after user cancellation
(`So6120SilentTrial.cs:260-280`). Temperature refusal stops experiment entry;
it does not block otherwise sound Auto cleanup. Fresh device, controller,
session, power, ownership and applicable readback admission still apply.

## Minimal useful guardian design

Build a developer-only injected prototype first. The useful boundary is a
**surviving sole owner**, not a second process that wakes and writes behind the
owner's back. The guardian owns the complete direct operation, native handle,
journal and checkpoint from before entry until confirmed Auto and release.
The caller sends typed `BeginApprovedTrial`, `RequestAuto` and `GetStatus`
messages. It never receives raw port access or owns an independently usable
cleanup handle. Existing exact-device and attended-consent checks remain.

Use one serialized state machine:
`Idle -> Preparing -> Entering -> StopObserved -> ReturningAuto -> Complete`,
with `Unresolved` terminal for further mailbox commands. Caller loss, explicit
Auto, a missed caller lease, or the operation's fixed duration requests cleanup
at the next resolved boundary. Once cleanup starts, renewed heartbeats cannot
cancel it or reenter Stop. A heartbeat does not renew an unlimited Stop lease.
No new thermal query, UI callback, console write or status consumer may block
the Auto decision. Use bounded status delivery outside the owner queue.

The guardian should keep in-memory transaction progress separate from liveness:
operation generation, current phase, whether native I/O is active, whether
publication is possible, last durable checkpoint, fence cause, Auto readback
and native release status. Preserve failure details and report **unresolved**
instead of synthesizing Auto when a status channel dies. Auto confirmation,
ancillary restoration, journal completion and handle release are separate
facts. A missing cleanup receipt must not trigger another Auto submission.

For a future service-owned integration, the service must remain the sole laptop
HID owner. A direct-port guardian would need an explicit, reviewed delegation
of the direct operation and exclusion of conflicting service thermal work;
this document does not grant that architecture or production transport
admission. Its process must survive loss of the requesting service, including
its actual parent/job lifetime. A restarted service must discover the existing
operation and remain unable to start a competing writer. The current attended
platform instead requires Service, Tray and Session Agent already stopped;
it cannot simply be reused while those processes run.

Do **not** add automatic takeover after guardian death in this first design.
A future recovery protocol would need a versioned journal with explicit
quiescent checkpoints, complete ownership/baseline bindings, a single durable
claim that fences the old generation, positively released old native ownership,
and fresh independent state/admission checks before one typed cleanup. It
must reject a partial/corrupt tail, outstanding publication/readback, uncertain
storage, changed boot/device/controller state or unexplained field drift.
Matching values are not proof that an uncertain command can be replayed.
The present inspector/format supplies no such resume capability. Even a
correct future clean-boundary takeover would cover only a subset of process
crashes and would not close the OS/native-hang boundary.

## Supervised guardian integration

The four supervised PWM commands now launch a separate Capture guardian. The
client holds no hardware resources and sends only heartbeats and an Auto
request through one inherited anonymous pipe. The owner performs both existing
BASELINE and APPLY prompts in its own console, creates the same one-shot journal
and attempt latch, and retains all exact-device, controller and thermal checks.
Default command invocation still prints an offline plan.

Planned v2 journals permit a read-only attended observer while the owner retains
exclusive write and delete access. The observer must validate complete chained
records and the run identity; a partial tail or an exit claim cannot authorize
a checkpoint action. Ordinary journal inspection remains available only after
owner release. Legacy v1 journals retain exclusive access.

The launcher uses `CREATE_NEW_CONSOLE`, `CREATE_BREAKAWAY_FROM_JOB` and an
explicit inherited-handle list. The client assigns itself to a private unnamed
job with breakaway and kill-on-close enabled. Its owning handle stays rooted
until client process teardown; disposing that last handle during normal client
execution would terminate the client before it could report its result.
Only the control pipe, a query-only job handle and an original-client process
handle with query and wait access cross into the owner. The owner verifies the job contract, the
original live client's membership and nonsignaled handle, its own absence from that job, sole console
attachment and readable control before trial metadata or acquisition. It closes
the temporary query handles before continuing so they cannot prolong the caller
job's lifetime. Assignment or verification failure refuses startup; there is no
ordinary-process fallback, alternate parent/token, restart or takeover.
These flags and handle-list requirements follow Microsoft's
[process creation](https://learn.microsoft.com/en-us/windows/win32/procthread/process-creation-flags)
and [startup attribute](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)
contracts. This is a supervised **caller-process and dedicated caller-job**
recovery contract. Ambient job membership is recorded, not identified as safe or
as belonging to Windows. Unknown outer host-job termination, closing the
guardian's own console, session logoff, sleep, native hangs and OS failure remain
uncovered. The owner prints this scope before either attended approval.

`<run-root>.guardian-startup.json` persists the startup stage, native error and
known isolation facts even if the owner console disappears. It is written once
with durable flushing, without creating the trial directory. A prior or partial
sidecar refuses reuse. The owner records isolation refusal before releasing its
inherited job handle, since an incorrectly attached owner might be terminated by
that release. Query handles close before any potentially blocking console output.
Successful admission is recorded after startup console reporting succeeds and before the hardware
runner is called. This file describes **startup admission only**, never the
eventual hardware state, Auto confirmation or native release.

`So6120TrialGuardian` receives the already approved session and runs the trial
and its disposal on one worker. Explicit Auto, caller disconnect, a missed
heartbeat or the fixed operation cutoff requests the existing trial runner's
Auto path. The first observed stop cause stays latched. Heartbeats use one
monotonic timestamp for both expiry checking and renewal; a late heartbeat
cannot revive an expired lease or extend the fixed operation cutoff. A separate
latch covers the approval waits and attaches before the execution worker starts.
The live control latch alone owns the five-second caller lease throughout.
The 45-second execution cutoff starts after APPLY and disarms at the existing
Restoring milestone, preserving the session's independent Auto limit. A stop
before handoff cannot fall into a Start/Attach race or authorize another
preparation read. Interrupted approvals are recorded as cancellation, rather
than an operator declining the trial.

The wrapper adds no firmware sequence. A pending command finishes its mandatory
confirmation and checkpoint before cleanup; an ambiguous result fences all
commands. Disposal happens only after the runner returns. Status uses a
single-slot channel that replaces stale reports, so an absent consumer cannot
block Auto. Reported phases are the runner's coarse milestones, not a native
I/O activity monitor. The 50 ms watchdog is cooperative: scheduling delay can
delay observation of expiry, and neither deadline bounds a native call or Auto.

The receipt separates Auto readback, ancillary restoration, journal completion
and verified native/acquisition release. Cancellation can coexist with confirmed
cleanup; it still makes the underlying trial unsuccessful. A disposal failure
leaves the receipt unresolved even when the journal is complete. Live composition
disposes the session and acquisition, then requires `CloseEvidence.Closed` and
successful ownership release. Session disposal or process exit alone cannot
confirm that release. The durable `guardianReceipt` is retained even when
execution fails before producing a `trialResult`; the `closed` row retains both
caller and guardian stop reasons. A canceled trial can restore Auto and still
exit with code 2; inspect these facts rather than treating exit status as readback.
Results and failure text are printed only after release and the durable terminal
row. The completion message is also saved, and the original client reports the
exit outcome and evidence path after the owner's console closes. An unconfirmed
native close keeps the owner and its retained resources
alive for external supervised reconciliation, even if console output fails;
there is no second console read, automatic exit or cleanup retry on that path.

The separate `OpenBlade.Guardian.TestHost`
executable uses synthetic admission and an in-memory port. It cannot acquire a
native port and is excluded from installed payloads. Its process tests launch
the fake caller and fake owner as independent children of the test fixture.
They check caller loss during a pending fake FE command and after the committed
zero-duty checkpoint. The same owner completes one handback, after mandatory
confirmation when a command is pending.
Additional fake tests exercise the actual native launcher, inherited pipe,
control latch and guardian: the caller's breakaway-enabled kill-on-close job can
close while the same owner survives and returns Auto from the zero checkpoint.
A restrictive job refuses launch, and unrelated inheritable handles are excluded.
The test host retains an ambient Windows job. The fake fixture now uses the
same caller-lifetime creation, inherited handles and isolation verification as
the live developer command; there is no fake-only exception to job admission.
Tests record the actual original caller identity and ambient membership without
claiming survival of that ambient job's termination.
This remains offline process evidence, not a live native-driver trial or a
future service-delegation contract. Killing
the fake owner leaves an incomplete journal and no receipt; the existing
one-shot journal refuses reuse without issuing commands. There is no restart
takeover or production backend integration.

## Bounded offline verification

Use injected memory ports, fake clock/evidence, temporary journals and a fake
native invocation that can be released by the test harness. Never open InpOut
or kill a process containing real native I/O for these tests.

1. At every resolved entry/stop checkpoint, disconnect the requesting process
   or expire its lease. Assert one cleanup sequence from the exact checkpoint,
   the original selector and unrelated bits preserved, and independent Auto
   confirmation before ancillary restoration/release. Include partial entry.
2. Disconnect during a pending fake transfer. Until it resolves, assert no
   cleanup, second owner or handle close. On confirmed completion, proceed
   once; on ambiguous completion, fence all commands, including A0.
3. Inject journal failures before write, before flush and after flush; malformed
   tails; confirmation failure; changed admission; and close failure. Assert
   precise retained cause, no automatic retry/restart takeover, no false Auto
   or release receipt, and preservation of the attempt marker.
4. Saturate/disconnect status consumers and stop heartbeats while the caller
   process remains alive. Assert neither UI backpressure nor an unrelated
   liveness thread controls hardware progress. A late heartbeat cannot undo
   cleanup. Assert Stop cannot be persisted or restored after restart/resume.
5. In a separate process harness with **fake hardware only**, terminate the
   caller and then the guardian at controlled boundaries. Verify the actual
   process/job lifetime and startup fencing. Guardian loss yields unresolved
   status, not guessed Auto. This validates process isolation, not firmware
   recovery.

Run the existing focused mailbox/session/journal/stop tests with the new
guardian tests. Their role is to prove ordering and failure handling; no
additional physical stop repetition is needed to re-prove the unchanged
command sequence.

## Earlier attended launch blocker (2026-11-07 UTC)

The authorized caller-loss trial on candidate `c5d52703` did not reach hardware. Both the elevated
prepared launcher and a standalone launch from Windows Start returned the
guardian's isolation-refusal code before metadata or acquisition. The earlier
restored attempt marker was archived and verified; neither launch created a new
hardware attempt. These results do not exercise zero duty or Auto restoration.

A separate probe linking only the native process launcher and isolation check
confirmed the job-membership refusal. The native child escaped an immediate
breakaway-enabled kill-on-close job but remained in an outer job. A local WMI
probe, including explicit breakaway creation flags, also retained job membership.
The named-job WMI provider returned no instances, leaving ownership unresolved.
No driver, hardware backend or Capture session was linked into these probes.

Windows [nested-job breakaway](https://learn.microsoft.com/en-us/windows/win32/procthread/nested-jobs)
can stop at an ancestor that forbids it. Windows
[Desktop Activity Moderator](https://learn.microsoft.com/en-us/windows/compatibility/desktop-activity-moderator)
can also assign desktop applications to system-managed jobs after creation;
that is a possible explanation, not an identification of the observed job.
[QueryInformationJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-queryinformationjobobject)
with a null handle describes only the immediate job. No kill-on-close flag is
not proof that an unknown job controller cannot terminate the guardian.

The replacement above establishes the narrower caller-process and private-job
contract through actual inherited object handles. It does not infer ownership
of an unknown job from its flags or process list. Repeated UAC or alternate shell
launches were ruled out as a solution. Hardware validation remains a separate
step after review and fake-hardware verification of the replacement.

## Remaining live question

The one useful next attended test is **caller loss with a surviving owner at a
confirmed zero checkpoint**. The current test remains authorized and unperformed.
Preserve that authorization within its scope; review the changed launch contract
and freeze the new candidate before proceeding. Reuse exact RZ09-0581, the reported
BIOS/EC pins, USB-C Balanced, original bounded trial, saved baseline and fresh
admission. After the already reviewed zero observation, deliberately disconnect
or terminate only a client with no hardware handle. Keep the owner and its
native I/O alive. Confirm autonomous selector-first Auto, original owned-field
restoration, durable completion and native release. Do not extend the stopped
interval to test an arbitrary lease. Record caller-loss detection and the
start/end of actual handback separately from software cutoffs.

This would validate caller-loss handling only. No guardian kill, native-hang
injection, OS crash, forced sleep or EC reset is justified by the current
recovery evidence. Attended sleep/resume would require its own approval and
concrete recovery procedure. Ordinary unattended Stop still requires an
independently supported cooling recovery mechanism; software-only guardian
tests cannot supply that missing property.
