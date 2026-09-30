# Cooling-pad fan restoration across a closed session

Tracking: [issue #119](https://github.com/OpenBladeProject/openblade-core/issues/119).
The successful immediate fan trial used one session. The passive USB reconnect
started from restored quiet Auto. Neither establishes that a fresh handle can
restore Auto after the Fixed session closes. This developer-only attended test
isolates that gap before a separate process-exit or crash experiment.

## Fixed sequence

1. Fresh exact-host/pad/controller checks and query raw `0000`; save that original
   value and the present operator's quiet Auto/full-brightness static-green baseline.
2. Acquire raw `0300` once, require exact acknowledgement, then query `0300`.
3. Send Medium `0D/10:01012C` once, require exact acknowledgement and separate
   physical Medium/normal-green/no-red-or-sleep confirmation.
4. Close the first actor normally. Require bounded disposal to finish successfully
   with a fully disposed disposition before opening another actor. A pending,
   timed-out or faulted close stops this experiment without further writes.
5. Open a new strictly admitted actor, freshly enumerate the current exact device,
   and query raw state. Require `0300`; it is an intermediate value, not the saved
   original baseline. An unexpected result stops without an alternate sequence.
6. Send captured Auto `0D/10:000600` once, require exact acknowledgement and a
   physical return toward the original quiet baseline with normal green lighting.
7. Restore raw `0000` once with exact acknowledgement, then query `0000`.
8. Obtain a separate final physical confirmation of normal quiet fan and unchanged
   full-brightness static-green lighting, without red/sleep.

Eight transaction identifiers are reserved before I/O and remain distinct across
both sessions. Controller guards apply to fresh opening and every exchange.
There are no arbitrary reports, alternate targets, retries or finally writes.
A failed exchange, ambiguous result, cancellation, timeout or rejected physical
observation stops further mutation. Preserve the actual result and report any
incomplete restoration; never infer physical success from an echo or raw mode.

## Scope and recovery

Exact target: RZ09-05289EN4 plus pad `1532:0F43`, revision `0200`, MI_00 usage
`000C:0001`, 91-byte Windows Feature buffer with prefix zero. BIOS versions are
provenance only. The installed product receives no fan writer or admission.
Synapse, Razer services and USBPcap remain stopped. Keep USB and 12 V connected;
pad-dependent HyperBoost must remain inactive. No disconnect or process kill is
part of this sequence.

The present operator must confirm the original baseline and remain available for
all three physical checks. The developer acknowledgement names the already
allowed manual power-cycle contingency only. The command performs no power cycle,
service/process operation or vendor recovery. The latest instruction forbids
agent-initiated Synapse use. A full USB-plus-12 V power cycle, if needed, requires
the operator's physical action and a separately recorded result; it does not
promise restoration of preferred settings without vendor software.

## Attended developer command

```powershell
dotnet run --project src/OpenBlade.Capture --no-build -- validate-rz09-0528-cooling-pad-fan-session-continuity --confirm-target RZ09-05289EN4:1532:0F43:0200:Medium2200-CloseFreshSession-Auto --confirm-recovery ManualPowerCycleOnly
```

Exact target/action and recovery arguments precede native actor construction.
Standard error contains the three physical prompts; standard output contains
only the structured result. Supply `YES` only after the relevant current operator
reply, never in advance. Exchange, first-close, observer and final-disposal waits
are bounded. A late close or observer cannot reopen a handle or resume writes.
## Evidence and next boundary

Record original raw baseline, all attempted/acknowledged stages, completed first
closure, new actor opening, fresh raw intermediate value and restoration, plus
three distinct operator observations. Hash private typed output and retain source
and build identity. Publish no raw reports, serials, device paths or process IDs.
No independent PCAP or native report trace is collected by this test.

Success establishes one same-process handle/session continuity experiment on this
exact host and pad. It does not establish new-process continuation, crash recovery,
interrupted Fixed reconnect, exact RPM/firmware fan state, ownership or production
handback. Review those distinct boundaries after this result; do not force a crash
or substitute an unbound continuation command during this experiment.

Implementation verification: all 657 Capture tests and three architecture tests
passed, including 46 continuity cases. Independent source, native-wiring and
documentation review passed, with a warning-free build. No live continuity trial
has been armed or executed.