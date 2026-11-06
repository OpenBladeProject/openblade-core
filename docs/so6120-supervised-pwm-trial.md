# Supervised SO6120 PWM increase

This developer-only runner prepares one attended direct-duty increase
on the exact RZ09-0581. Its implementation and simulated tests do not establish
physical PWM control, fan stop, mailbox exclusion or physical recovery. No
live mailbox trial is included in preparing or reviewing this runner. The
[attended Balanced attempts](so6120-balanced-pwm-trial.md) include a successful
half-cycle trial: selector FE, actual PWM `91/91` through the observation,
and independently verified Auto restoration. Zero duty, commanded fan stop,
Silent direct duty and unattended recovery remain unverified.

## Fixed experiment

Two fixed entry commands admit **Balanced with Automatic fans** or **Silent
with Automatic fans**, on external power, awake and idle or doing light
desktop work. Use the Balanced command for the USB-C Balanced experiment;
keep the current profile rather than switching to Silent. Admission pins the experimental host SKU,
BIOS 4.01, reported EC 1.04, exact control HID collection and the already
running reviewed InpOut driver. Reported EC version is not a measurement of
the running image's hash. The offline EC image hash remains source provenance.

Balanced is selector `00`; Battery Balanced is a different selector, `06`,
and is not admitted by either fixed experiment. The recovered
[FE writer](so6120-ec-pwm-writers.md#fixed-address-stores) checks active state
and F0.6 without a Silent-only or connector predicate. This supports reusing
the test sequence, not a claim that USB-C fan stop has been observed.
Balanced's factory Automatic table also contains a zero-target stage.

USB-C is operator-reported context. Fresh Windows AC-line status confirms
external power; it does not distinguish USB-C from a full-power adapter or
detect every swap between powered connectors. Keep the power connection
unchanged throughout the attempt. The runner adds no power-classification
HID command and makes no claim of independent connector verification.

Capture actual PWM, cycle, selector, manual flag, override, staging, policy
and active-state bytes through ordered bank-zero A0 reads. The readings are
not an atomic snapshot. The +4 commands derive **actual PWM + 4 raw counts on each fan**;
both need at least four counts of headroom. Do not clamp or silently
recalculate the targets. Display the complete baseline and derived plan,
then require approval of that exact in-memory plan and captured profile. The
fixed command, journal identity and baseline selector must agree.

New plans allow normal Automatic PWM drift: at each existing entry-write
check, each fixed target must be **1–8 raw counts above fresh actual PWM**.
For example, a target of 54 remains admitted while actual PWM is 46–53.
This replaces exact telemetry equality; mapped controls and ownership must
still match. The rule is included in the approved plan hash and checked by
journal replay. Legacy plans retain their strict equality rule and original
hash. Targets are never recalculated, and a zero, negative or larger live
increase refuses the next write. Exact target readback after FE and during
the dwell is still required. These sequential reads cannot eliminate the
read-to-publication race.

A separate fixed Balanced command, `validate-so6120-balanced-pwm-half-cycle`,
derives both targets as `floor(captured cycle / 2)`. At the observed cycle 183,
that is `91/91`. Its hash-bound policy requires each fixed target to be strictly
above the fresh actual reading at every existing entry gate. It has no raw-value
option, target rebasing or extra setting write. It is intended to tolerate an
Auto startup transition such as `0/0` to `38/38`; it refuses equality or a higher
reading on either fan. This permits a larger increase than the +4 trial,
and the console explicitly requires approval of its own targets and policy.
Half-cycle is a ratio of the raw counter, not a calibrated electrical-duty
percentage or fan RPM. Legacy +4 plans retain their original behavior and hash.

There is no preparatory RPM command, zero target or decrease. A natural
zero baseline is allowed; a request of four counts might not start the fan.
The mapped tach bytes are reported without an asserted RPM conversion. The
HID RPM response's scale is not automatically applicable to these EC fields.

The fixed sequence stages B8/B9, enables the owned F0.6 permission and
publishes selector FE last. Every A1 has a separate admitted A0 confirmation
and mapped-state observation. Observe actual PWM, wait one second, observe
again and explicitly return to Auto. Handback restores the captured normal
selector directly, confirms Manual00, then restores only the changed owned
override and original staging bytes. It does not restore sampled actual PWM
over firmware Automatic, send a paired HID Auto command or detour through
another profile. Balanced restores `00/00` directly; Silent restores `05/00`.
The handback implementation remains generic for normal profiles; these two
fixed experiments do not admit the other normal selectors for live entry.

F0.6 affects another gated policy contributor as well as permitting FE.
Exposure therefore begins at possible F0.6 publication, before direct entry,
and ends only when the original permission is confirmed restored. Initial
F0.5/F0.6, inactive state, pending policy command or unexpected manual state
reject entry. Idle policy command is not proof that event14 work is drained.

## Ownership and operator approval

Before starting the live command, use the installed application's normal
**Shut down OpenBlade** action. Do not merely close its window. The runner
requires the service stopped and its writer, tray and Session Agent processes
absent; it does not stop
them or any Razer software. It changes no saved installed settings. It never
starts another controller during its lifetime.

Metadata inspection opens zero-access HID descriptor handles without sending
feature reports. A first interactive token then accepts the proposed scoped
residual risks and authorizes one InpOut acquisition and baseline. A second
token, containing the plan ID and digest, authorizes the derived increase and
its Auto handback. Each prompt has a 60-second wait; cancellation or timeout
ends the attempt, with no second input reader after a timeout. JSON records
and matching hashes are evidence of comparison, not independent authority.

The prior [InpOut handle control](so6120-mailbox-calibration.md) qualified
name visibility for one zero-access lifetime only. No physical-action
challenge is claimed by this runner. Its proposed acceptance explicitly
retains unnamed handles, uncovered ACPI paths and other unobserved writers.
The operator can accept these finite limits for this fixed short test; the
runner does not reinterpret acceptance as proven exclusion.

Fresh scope fingerprints cover present device-node IDs and the selected
configured driver/Razer/OpenBlade service entries. They do not attest all
loaded images, device filters, SMM or firmware writers. Keep peripherals and
software configuration unchanged. Exact host, power, session, driver and
known-controller checks remain fresh at command admission.

A fixed cross-process guard excludes another instance of this runner. A
machine-local, create-once attempt latch is retained after the run; a new
output directory cannot bypass an unresolved earlier attempt. It is not a
hardware lock or an administrative security boundary. A subsequent trial
requires offline review of the retained journal and explicit reconciliation
of that latch; there is no automatic reset or replay option.

## Bounds and abort behavior

Entry requires a fresh CPU thermal-zone reading of 10–55 °C and confirmed
inactive GPU. Unknown, stale or warmer readings stop entry. These are
conservative experiment limits; the ACPI thermal zone is not asserted to
measure every CPU hotspot. High or unavailable temperature alone does not
prevent otherwise admitted Auto restoration.

Entry stops new A1 publications and the optional dwell after 45 seconds, or
30 seconds from possible F0.6 publication. These are cutoffs at resolved
boundaries: observation batches, an already-started write, its independent
confirmation and full-state checkpoint can finish after the cutoff.
The dwell is one second. Approved Auto handback has its own 60-second phase,
checked before each new logical transaction. Each admitted transaction keeps
its full two-second budget, including independent write confirmation; its
remaining time is not shortened merely because a phase cutoff approaches.
Auto can overrun its phase cutoff by one in-flight transaction even without
a native stall. Full metadata collection, durable flushes and thirteen-read
snapshots consume time. The trial records actual phase and exposure milestones;
these limits do not guarantee a maximum physical exposure or successful exit.

Ctrl+C requests cancellation at a resolved boundary. A confirmed partial
entry may take its approved Auto path after fresh admission. An ambiguous
native command, confirmation, state, ownership check or journal fences all
mailbox commands, including A0. A0 publishes a request and is not passive
reconciliation. There is no guessed cleanup in `finally` or `Dispose`.

Optional console progress is buffered until restoration or fencing, so a
paused console cannot delay an otherwise admitted handback. Mandatory
transaction evidence remains durable. Synchronous native calls and storage
can stall beyond software deadlines.
There is no proven unattended watchdog. Do not kill the worker, close and
reopen the driver, launch another writer, click GUI Auto or improvise a
reboot as an assumed restoration method. If stalled or unresolved, leave
OpenBlade stopped, stop workload, retain the worker/console and evidence,
and reconcile under supervision. Physical recovery from such a state has
not been established. This residual is part of the proposed approval,
not a claim that restoration is guaranteed.

## Running the reviewed candidate

The default command prints the plan and never collects metadata or acquires
hardware:

```powershell
OpenBlade.Capture.exe validate-so6120-silent-pwm-increase --plan
OpenBlade.Capture.exe validate-so6120-balanced-pwm-increase --plan
OpenBlade.Capture.exe validate-so6120-balanced-pwm-half-cycle --plan
```

Only after separate attended execution approval, run the reviewed binary
in an elevated interactive console with a new, absolute, local directory:

```powershell
OpenBlade.Capture.exe validate-so6120-balanced-pwm-increase --run-root C:\path\to\new-attempt
```

There is no `--yes`, raw-register, raw-value, zero-duty or resume switch.
The Silent command remains available for a separately approved Silent trial;
the half-cycle command is fixed to this Balanced experiment. None accepts a
replacement profile or recalculates a changed plan.
The separately prepared local test bundle records complete payload hashes;
its launcher pins that manifest, refuses a changed payload and observes the
actual child exit. That local artifact is not part of the repository or an
installed feature. Direct CLI invocation does not independently enforce the
bundle manifest. Preparing and checking the bundle never starts the trial.

Keep `attended-run.jsonl` and `so6120-direct-duty.journal` locally. They may
contain private device paths and machine metadata and are not release assets.
The journal retains publication intents and real results, refuses reuse,
and is inspectable but never resumable. A reported successful trial requires
the observed PWM response, exact Auto/owned-field cleanup, a durable terminal
record and confirmed native handle release. A safely restored cancellation
is reported separately from a successful trial.

After verified restoration, close and actual process exit, manually reopen
OpenBlade through its normal shortcut. Confirm the captured profile/Automatic and unchanged
saved intent through normal status. If any completion, close or restoration
result is unresolved, leave it stopped and review the retained evidence.
Normal Auto may naturally report zero RPM; that alone does not invalidate
selector/manual/permission restoration.

No other Blade model is admitted by this test. Shared software changes do
not generalize this host's firmware addresses or physical results.
