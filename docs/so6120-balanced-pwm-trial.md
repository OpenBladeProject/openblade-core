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
