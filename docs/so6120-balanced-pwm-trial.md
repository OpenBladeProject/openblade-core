# SO6120 Balanced PWM trial and polling correction

The first attended USB-C Balanced attempt on 2026-11-06 stopped during
staging, before enabling direct PWM. It used reviewed candidate
`6449acebaf68ca70de1c1015c58db886ce3529ab` on the exact RZ09-0581,
BIOS 4.01, reported EC 1.04. The operator approved the captured baseline
and the derived increase. USB-C was operator-confirmed context.

| Field | Captured baseline |
|---|---|
| Selector / manual / override | `00 / 00 / 00` (Balanced / Automatic) |
| Staging B8 / B9 | `0 / 0` |
| Actual PWM / cycle | `50 / 45`, cycle `183` |
| Requested PWM | `54 / 49` (+4 raw counts each) |
| Raw tach bytes | `22 / 21`; no asserted RPM conversion |

The B8 write completed and a separate A0 read confirmed `54`. The B9 write
reported handler completion, but its separate A0 confirmation exhausted
128 polls while command register `93` still read `A0`. This was **PollLimit**,
not expiry of the two-second transaction deadline. The saved timestamps bound
the whole second transfer and fencing to less than 401 ms; the confirmation
polling window was shorter. No native I/O error was reported.

The journal's 392 records passed sequence and hash-chain verification.
Only B8 and B9 setting writes were published. No F0 permission or FE selector
write occurred. The last complete observation remained Balanced/Automatic,
override zero, actual PWM `50/45`, and staging `54/0` before the B9 write.
The later B9 value was not independently confirmed. No restoration ran.
The journal ended with a durable fence, the native handle closed successfully,
and the parent observed child exit code 2.

This proves one mapped staging write and its readback. It does not establish
direct fan control, fan stop, a firmware hang, or successful restoration.
The original journal, launch claim, and machine attempt marker remain intact.

## Narrow correction and reconciliation

The transaction core now spaces pending polls over the existing time budget:
16 ms at the normal two-second/128-poll setting, capped by the remaining
transaction time. A1 and its independent A0 retain one shared deadline.
Cancellation, command changes and native failures still stop the operation;
the fix neither republishes a request nor resumes the fenced attempt.

A separate read-only observer can sample the existing host
mailbox envelope. It selects fixed host indices and reads their data without
writing the data port or publishing an EC command. An idle, matching envelope
would be consistent with late completion, but has no transaction identifier
and cannot independently prove current mapped fan state or restoration.
Further mapped reads or restoration need their own reviewed, approved scope.

The default command prints its plan without metadata collection or driver access:

```powershell
OpenBlade.Capture.exe observe-so6120-mailbox --plan
```

The separately approved live form takes the original attended-attempt directory
and a new output directory:

```powershell
OpenBlade.Capture.exe observe-so6120-mailbox --prior-run-root C:\path\to\prior-attempt --run-root C:\path\to\new-observation
```

It requires an intact fenced journal without possible duty permission, confirmed
prior native close and child exit, the retained matching machine marker, current
scope matching the prior run, and fresh attended admission. The displayed approval
binds the prior identity and journal/log/receipt hashes. The local prepared launcher
additionally pins those exact three artifacts through child exit; direct CLI use
does not independently enforce those local pins. A failed native close is reported
without retry; the driver pin and run reservation last through reporting, with
retention ending at process exit. Neither result resumes the old attempt.

## Completed envelope observation

The separately approved observation ran with candidate
`d5dfe76601c29f2f575d36789150b744361a75f4`. Both scans matched:

| Host mailbox field | Observed value |
|---|---|
| Bank / index / count | `00 / B9 / 01` |
| Command / status | `00 / 80` (idle / success) |
| Payload | `49` decimal (`31` hex) |

Each scan began and ended with command `00`. The operation made fourteen
fixed index selections and fourteen data reads, without publishing an EC
command. Native close was confirmed without errors, and the parent observed
child exit code 0. The prior failed attempt and its marker were preserved.

This is consistent with the pending confirmation completing after polling
stopped. The mailbox has no request identity, and its payload was already
staged before that confirmation. These observations therefore do not establish
the current mapped B8/B9 values or successful restoration. Direct PWM remains
unverified. The next operation must independently read the current mapped
controls before restoring any inactive staging.

## Fixed staging recovery

`restore-so6120-staging --plan` describes a separate developer operation pinned
to this failed trial and the completed observation. Its attended form takes
`--prior-run-root`, `--observation-root` and a new `--run-root`. Approval binds
both locked evidence sets and the current exact device/controller scope.
A separate machine claim is consumed before the first mapped A0 read.

Recovery reads only the eight required fields: selector, manual, override,
B8, B9, cycle, policy command and active state. It requires the original
Balanced/Automatic controls, override and policy command zero, cycle 183 and
active state. B8 must be zero or 54; B9 must be zero or 49. Changed staging
bytes are restored to their original zero values with separate A0 confirmation
and complete control checkpoints. Already-zero bytes cause no setting write.
Snapshot age is checked after its durable record is flushed.

Only B8/B9 zero writes are admitted. Unexpected state, expired observation,
uncertain I/O or storage failure stops all further commands. Final mapped
readback, durable recording, confirmed native close and child exit must all
succeed before claiming restoration. The original journal and attempt marker
remain unchanged; recovery neither rearms the trial nor enables direct PWM.
The initial recovery preparation did not grant execution authority.

The separately approved recovery subsequently completed on the same laptop
using candidate `25740ef30e73dbcac26f14fb824689d0e2cb4967`. Its fresh initial
snapshot confirmed B8/B9 at `54/49`, with Balanced/Automatic, override and policy
command zero, cycle 183 and active state 161. It restored B8 to zero and
independently confirmed it, then did the same for B9. Complete checkpoints
showed `0/49`, then `0/0`, and a final independent snapshot again showed `0/0`
with the original controls intact. Both setting writes and all mapped reads
completed successfully. Native close was confirmed without errors, and the
parent observed child exit code 0.

Staging restoration is now physically verified for this attempt. Recovery
preserved the old attempt marker. Direct PWM and fan stop remain unverified;
another trial requires a new approved run with a fresh baseline and its own
immutable +4 plan.

## Paced trial stopped on natural Auto drift

After separate approval, the recovered original marker was archived without
changing its bytes, and a new trial used the same tested candidate. The fresh
baseline showed actual PWM `44/41` and raw tach `20/19`; the approved targets
were `48/45`. Before the first setting write, a new snapshot showed actual PWM
and raw tach both `0/0`, with the original Balanced/Automatic controls intact.
Entry rejected the changed baseline. The 110-record journal verified through
EOF and contained 26 successful A0 publications and no A1 or setting intent.
Native close was confirmed without errors. No setting restoration was needed.

The wrapper observed Capture's exit but recorded a null exit code. Its parent
rejected that receipt instead of treating it as success. The exact numeric
exit code is unavailable; subsequent process inspection found both processes
absent. The frozen launcher and receipts are retained unchanged. This is a
process-reporting issue, separate from the verified no-setting-write result.

Harmless child processes reproduced the reporting issue in Windows PowerShell
5.1: `Start-Process -NoNewWindow -PassThru` followed by `WaitForExit` returned
null exit codes for actual exits 0, 2 and 3. Starting the child directly through
`Process.Start` with `UseShellExecute=false` retained its creation handle and
reported all three codes correctly. The reviewed wrapper correction preserves
the attended console and fixed command, captures the child PID immediately,
and rejects a missing numeric exit code. It does not invent the old result.

The next trial must derive and approve a new +4 plan. Keeping the old targets
after PWM fell to zero would instead request a much larger increase. Natural
Automatic zero readings do not establish that the direct PWM path controls
fan stop.

## Permission-prefix trial restored after PWM changed

The next prepared wrapper exited before Capture started. Its historical error
was not captured, so its cause is unknown. A reviewed continuation preserved
that failed wrapper's evidence, reconciled the fixed old marker path, and used
the still-unused hardware-trial approval. The old marker was found at its
original path and archived with its bytes verified. The frozen Capture candidate
remained `25740ef30e73dbcac26f14fb824689d0e2cb4967`.

The resulting attended run `1092b8f0-cbc5-4a97-aacf-38e34c85396c` began in
operator-reported USB-C Balanced/Automatic with actual PWM and raw tach `0/0`,
override and staging zero, cycle `183`, policy command zero, and active state
`161`. The approved
+4 targets were therefore `4/4`. B8=`04`, B9=`04`, and F0=`40` each completed
with a separate successful A0 confirmation.

The first complete snapshot after F0 showed actual PWM `37/37` and raw tach
`17/17`; subsequent snapshots reached PWM `39/39`, then `40/40`. The selector
remained Balanced `00`, manual remained `00`, and staging remained `4/4`.
CPU temperature observations were `45 C`, with the GPU reported inactive.
The timing associates the rise with permission publication but does not prove
that publication caused it.

The selector `1A=FE` was **never submitted**. Its durable intent was followed
by an explicit not-submitted record, with zero possible publications, zero
completed commands and no native operations in that transfer. The fresh
actual-PWM comparison stopped entry with `PwmNotObserved` because the approved
baseline had changed. A `4/4` target would have requested a decrease from the
observed `40/40`.
This was a local admission refusal, not firmware rejection or overwrite of FE.

The confirmed-prefix handback restored only the owned fields: F0=`00`, B8=`00`,
and B9=`00`, each with a separate A0 confirmation. No selector write was needed
because FE had not been published. Final independent snapshot 25 confirmed the
original controls, staging and cycle; actual PWM was `59/51`, under Automatic.
The journal recorded completion at generation 7 without a fence or remaining
permission exposure. Native close succeeded without errors or release failures.
The wrapper observed Capture exit `2`; the parent recorded both Capture and
wrapper exit `2` and observed the actual processes exit. That code reports the
unsuccessful direct-duty trial; it does not negate the independently verified restoration.

All 1,382 journal records passed sequence and hash-chain verification through
EOF. The 337 native command publications comprised 331 A0 reads and exactly six
A1 writes: three entry-prefix writes and their three restorations. The attempt
marker and executed artifacts are retained. Direct PWM and commanded fan stop
remain unverified. Another trial requires a separately approved plan that stays
an increase at the point of direct-mode entry; it must not bypass this drift
refusal or reuse the consumed run.

Offline inspection of the pinned EC image is consistent with ordinary Auto
startup. Selector `00` follows the normal fan path at `15E66–15E80`; the
F0.6 fan-dispatch checks are in the `FF` and `FE` branches. Factory floor
records `29CC4/29CCC` specify 20%, converted by `14422–14442` and `166A6`
to 36 counts for cycle 183. The normal large-deficit feedback branches at
`155EA–15606` and `15796–157B2` apply that floor and increment it, producing
37. This explains the shape of the observed ramp without establishing its
trigger. F0.6 also affects a separate policy contributor; an indirect effect
has not been ruled out.

## Tolerating ordinary Automatic drift

New prepared plans retain fixed targets of captured PWM +4, but admit a live
increase of 1–8 counts per fan at each existing entry gate. Exact target
readback and Auto restoration remain required. The rule is included in the
plan hash, while old plans keep their strict equality semantics. This avoids
rejecting a small natural variation solely because it differs from the
captured sample. It does not admit a target below the fresh reading: the
observed `4` target against `40` would still refuse direct entry. No targets
are silently rebased and no extra writes or retries are introduced.

## Bounded trial encountered an Auto startup transition

The separately approved trial of `ba5f161a6a4cfe1b2d68f527c0166dd905e46260`,
run `854d11c2-3e03-4a83-8aa6-cbe63db6600e`, approved the new bounded rule and
fixed targets `4/4` from another `0/0` baseline. B8=`04`, B9=`04` and F0=`40`
were independently confirmed. The first two complete snapshots after F0 still
showed PWM and tach `0/0`; the next showed PWM `38/38`, with raw tach `17/18`.
The `4/4` target was below the fresh reading, so FE was definitely unsubmitted.

The runner independently restored F0, B8 and B9 to zero. Final snapshot 25
confirmed the original Balanced/Automatic controls, cycle 183, idle policy
command and active state 161; Automatic PWM was `48/41`. The journal completed
without a fence. All 1,382 records verified, matching 331 A0 publications and
six A1 writes. Native close succeeded with no release failures. Capture and
the wrapper both recorded exit 2; the outer PowerShell invocation's exit 1 is
a separate observation. The prior run's marker was archived with its bytes
preserved, and this attempt's marker remains retained. Direct PWM is still
unverified.

This was an Auto startup transition, beyond the range of a tiny-increment
plan. The next fixed experiment derives half the captured cycle, rounded down,
and requires each target to stay above its fresh reading. At cycle 183, the
fixed targets are `91/91`; `38/38` would satisfy the PWM comparison. This
larger absolute target needs its own attended approval. It is a raw count
relative to the cycle, not a calibrated electrical-duty or RPM claim.

## Half-cycle trial confirmed direct PWM and Auto restoration

The separately approved half-cycle trial used candidate
`b01b548c3dde536c389caa7c8ef72f439feb37bc` on the same exact laptop in
operator-reported USB-C Balanced. Run
`49ca8789-76b3-47a7-9fc9-50565a3ac293` captured cycle 183, Automatic controls,
zero permission and staging, and actual PWM/tach `0/0`. The operator approved
fixed targets `91/91` under the half-cycle policy.

The last complete pre-FE snapshot read actual PWM `46/41`. The runner
independently confirmed B8=`91`, B9=`91`, F0=`40` and selector `FE`. A complete direct-mode observation read actual PWM `91/91`, with raw
tach `38/38`. After the requested dwell, another complete observation still
read selector `FE`, permission `40`, staging and actual PWM `91/91`; raw tach
was `37/38`. Both channels matched the approved targets at all three complete
direct-mode observations, with nonzero raw tach readings on both fans. These raw tach bytes are not asserted to be calibrated RPM.

Auto handback directly restored selector `00`, confirmed Manual `00`, and
independently restored F0 and both staging bytes to their saved zero values.
The final complete snapshot retained cycle 183, idle policy command and active
state 161, with Automatic PWM `75/75` and raw tach `32/33`. Actual PWM was not
forced back to the zero baseline; it was again controlled by Automatic.
The trial reported success, confirmed permission restoration with no remaining
exposure, and no command fence. Native close succeeded without errors or
release failures. The wrapper observed Capture exit 0. The parent observed
wrapper exit 0, verified the Capture receipt, and returned 0.

All 1,714 journal records passed sequence and hash-chain verification. The
419 native publications comprised 411 A0 reads and eight A1 writes, each
with distinct one-byte A0 confirmation. The journal completed at generation 9,
referencing final observation 31. Recorded execution took about 44.9 seconds;
the interval from possible F0 publication to confirmed permission restoration
was about 21.3 seconds. The requested one-second dwell is separate from
entry, observation and handback overhead; it is not a one-second physical
exposure guarantee.

This confirms the reviewed SO6120 direct PWM entry and Auto restoration for
this one Balanced half-cycle trial. It does not establish direct zero duty,
commanded fan stop, lower steady RPM, Silent direct duty, unattended recovery,
or applicability to another Blade model. The raw evidence and completed
attempt marker remain retained. No production fan-stop capability is enabled.
