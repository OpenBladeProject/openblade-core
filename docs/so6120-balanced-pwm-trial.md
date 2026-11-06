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
