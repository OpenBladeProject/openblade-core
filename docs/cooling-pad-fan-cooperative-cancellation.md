# Cooling-pad cancellation between commands

Tracking: [issue #119](https://github.com/OpenBladeProject/openblade-core/issues/119).
Normal process-exit/new-process restoration succeeded in one attended trial. The
next developer experiment tests cooperative cancellation while Medium is stable,
with no HID exchange in flight and the supervisor still alive.

## Fixed cancellation boundary

Keep the original operator-confirmed quiet Auto/full-brightness static-green
baseline and raw `0000` in the live supervisor. Child 1 uses captured transactions
1–4 to query `0000`, acquire/query `0300` and acknowledge Medium `01012C`.
After the fresh physical Medium check, it enters a fixed cancellable wait and
reports that cancellation is armed. There is no pending HID command.

The supervisor sends one authenticated phase-specific cancellation instruction.
Only the child's work token is canceled; the control channel and native cleanup
remain available. The child must positively observe cancellation, complete
native disposal with `FullyDisposed`, send the matching cancellation receipt and
exit normally with the designated cancellation code `4`. A receipt alone, an
unexpected exit or an unobserved cancellation cannot authorize continuation.

After that actual exit, obtain another physical Medium/normal-green/no-red-or-sleep
check. Only then launch child 2 once. Fresh exact admission and transactions 5–8
query intermediate `0300`, acknowledge Auto `000600`, obtain physical quiet-Auto
confirmation, restore/query the supervisor's original `0000` and obtain the final
normal-green confirmation before complete disposal and exit zero.

## Admission and failure handling

Use a separate exact command and trial identity. Preserve the normal-exit
experiment's receipt-plus-exit-zero rule. Both trials use the same reviewed
current-user pipe, native PID/OS-parent and loaded/disk-build authentication,
fixed device identity and live stage permissions. BIOS versions are provenance;
only exact model/SKU and pad identity gate this experiment.

Actual supervisor cancellation, channel loss, malformed/stale phases, ambiguous
submission, failed acknowledgement, missing receipt, incomplete disposal or
unexpected process exit stops further writes. None becomes a recovery permit.
No retry, process kill, saved continuation file or firmware write in cleanup is
part of this trial. Keep late callbacks bounded and incapable of resuming work.

Build committed source, record exact apphost/DLL hashes, run focused tests and
actual hardware-free child-process checks, and obtain independent review before
live use. Then obtain the present physical baseline. USB and 12 V stay connected,
HyperBoost stays inactive, and Synapse, Razer services and USBPcap remain stopped.
Manual power cycling is the attended contingency, with no promise it restores
preferred Auto/green settings without vendor software.

## Evidence boundary

Record baseline, fixed sequence, cancellation observation, completed disposal,
actual exit code, post-exit physical Medium, fresh Auto/raw restoration and final
physical state. Publish sanitized stage results and build/artifact hashes; omit
PIDs, pipe names/nonces, paths, serials and raw reports. No independent PCAP is
collected. Five fresh physical answers are required: baseline, Medium,
post-cancellation Medium, Auto and final.

Success would establish cooperative cancellation between commands with orderly
native disposal. It would not establish forced termination, cancellation during
native I/O, interrupted Fixed reconnect, sleep/power loss, firmware fan-state/RPM,
ownership, crash recovery or production handback.

## Attended command

```powershell
OpenBlade.Capture.exe validate-rz09-0528-cooling-pad-fan-cooperative-cancellation `
  --confirm-target RZ09-05289EN4:1532:0F43:0200:Medium2200-CooperativeCancel-NewProcess-Auto `
  --confirm-recovery ManualPowerCycleOnly
```

The five prompts are `baselinePhysical`, `fixedPhysical`,
`postCancellationPhysical`, `autoPhysical` and `finalPhysical`. Answer each only
after the present physical observation; never prefill later answers. A separate
`selftest-cooling-pad-fan-cooperative-cancellation` command exercises simulated
actors exclusively in actual child processes.

The canceled token belongs to the developer experiment's deliberately pending
idle wait. This does not demonstrate cancellation of a production work loop or
an already submitted firmware/native operation.

Implementation checks passed 683 Capture tests, three architecture checks,
19 actual hardware-free cooperative subprocess scenarios plus direct invocation,
and the unchanged normal-exit harness's 18 scenarios plus direct invocation.
Independent source/tests/documentation review passed. Exact committed-source rebuild and host subprocess verification precede the next physical baseline.
No cancellation trial is armed or completed.
