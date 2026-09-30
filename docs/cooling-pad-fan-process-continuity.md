# Cooling-pad fan restoration across normal process exit

Tracking: [issue #119](https://github.com/OpenBladeProject/openblade-core/issues/119).
The same-process handle continuity trial succeeded. This next attended developer
experiment isolates normal process exit and a new process before any abrupt-stop
or crash test. No process is force-terminated as part of preparation or execution.

## Original baseline and two fixed phases

The short-lived supervisor retains the original operator-confirmed quiet Auto,
full-brightness static-green baseline and the first child's raw `0000` in memory.
It accepts no saved continuation file, executable path, command bytes, transaction
offset or alternate device as input.

Child 1 freshly admits exact RZ09-05289EN4 plus pad `1532:0F43` revision `0200`,
MI_00 usage `000C:0001`, 91-byte Feature buffer with prefix zero. BIOS versions
remain provenance only. Fixed transactions 1–4 perform query `0000`, acquire/echo
`0300`, query `0300` and Medium `01012C` acknowledgement. A separate physical
Medium/normal-green/no-red-or-sleep check follows. The child completes native
actor disposal and confirms the fully disposed disposition before its receipt.

The supervisor requires the matching completed receipt and actual normal child
exit code zero before launching child 2, once. Child 2 repeats fresh exact-device
and controller admission. Fixed transactions 5–8 query intermediate raw `0300`,
acknowledge Auto `000600`, restore/echo original raw `0000` and query `0000`.
Separate quiet-Auto and final normal-green physical checks are required. Raw
`0300` never becomes the original baseline or the restoration target.

## Transient process binding

Both children use the same fixed Capture apphost and DLL build as their supervisor.
An unpredictable one-shot duplex named pipe restricted to the current user binds
the run, child role, build and message sequence. The supervisor verifies the native
pipe-client PID equals the child it launched. Each child verifies the native server
PID equals its actual OS parent, with matching Capture apphost/build identity.

Missing or mismatched channel, parent/client identity, nonce, role, build or stage
must fail before native factory construction. Direct child invocation must not
create a hardware session. Persisted journals are evidence, never reusable write
authority. Every mutation requires a fresh live stage permission over that channel.

Channel loss, cancellation, malformed or stale messages, failed exchanges,
unexpected raw values, rejected physical observations, incomplete disposal or
nonzero/ambiguous exit stop further writes and prevent another child launch.
A permission received immediately before channel loss cannot undo a submitted
operation; preserve that ambiguity instead of claiming instantaneous prevention.

Connection, frame length, message reads, physical waits, native closure and normal
process exit are bounded. Failure requests cooperative cancellation and waits
boundedly; it must not force-kill a child or replay a phase. A hung or ambiguous
child needs attended reconciliation without an automatic restoration sequence.

## Attended and hardware-free gates

Before live mutation, build exact committed source and record apphost/DLL hashes.
Complete source/tests review and one actual hardware-free subprocess integration
check in the host environment. Its actors must be simulated and incapable of
calling the native pad factory. Exercise real PID/build binding, direct-child
rejection, channel loss and receipt-versus-process-exit ordering. Report those
results as process/channel validation only.

Then obtain the present operator's current quiet Auto/full-green baseline,
inactive pad-dependent HyperBoost and readiness for three physical checks. Keep
USB and 12 V connected. Synapse, the two named Razer services and USBPcap remain
stopped. No lighting, demand update, service operation or vendor recovery is part
of this experiment. Manual power cycling remains the already permitted attended
contingency, with its actual outcome recorded separately and no promised restoration
of preferred settings without vendor software.

## Evidence limit

Publish sanitized source/build hashes, typed phase results, disposal/normal-exit
ordering flags and three operator observations. Exclude process IDs, pipe names,
nonces, paths, serials and raw reports. No independent PCAP/native trace is collected.

Success establishes one normal process-exit/new-process restoration on this exact
host and pad. It does not establish abrupt crash, interrupted Fixed reconnect,
sleep, power loss, exact RPM/firmware state, ownership or production handback.
The guarded command is:

```powershell
OpenBlade.Capture.exe validate-rz09-0528-cooling-pad-fan-process-continuity `
  --confirm-target RZ09-05289EN4:1532:0F43:0200:Medium2200-NormalExitNewProcess-Auto `
  --confirm-recovery ManualPowerCycleOnly
```

It first asks for the present physical baseline, then waits for a separate `YES`
at Medium, restored quiet Auto and the final restored baseline. Each wait is
bounded to 180 seconds; an absent or negative reply stops further writes.
Run `OpenBlade.Capture.exe selftest-cooling-pad-fan-process-continuity` for the
hardware-free subprocess checks. That path uses simulated actors exclusively.
Independent source review passed, alongside 674 Capture tests, three architecture checks and 18 actual hardware-free subprocess scenarios plus direct-child rejection. These checks establish process/channel behavior only. No live trial is armed.