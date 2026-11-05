# SO6120 mailbox trigger test plan

Status: scripts prepared; live execution pending. No new trace, transition, device open or firmware call
has been performed for this plan. It follows the
[passive observations](so6120-mailbox-runtime-observation.md).

The [prepared scripts](../tools/mailbox-observation/README.md) implement the
bounded recorder, saved-trace export/analysis and offline image inspection.
Their tests use fake sessions and existing captures; the new recorder has
not run live. Calibration checks collection mechanics before action windows;
marker validity is determined offline afterward and can invalidate a window.

The question is whether ordinary actions activate another mailbox client on
this exact RZ09-0581 / SO6120 configuration. A clean, adequately observed
challenge can justify treating contention as a low-priority residual risk
for one short, attended Silent experiment. It need not prove that no caller
could ever exist. This plan does not quantify collision probability, enable
production support or authorize a direct-PWM write.

## What to observe

Collect Kernel-Acpi method events and Handle-only System Object events over
the same timestamped action windows. Search for `TBCC`, `ECRR` and any other
source-confirmed method reaching ports `300/301`; distinguish those methods
from unrelated EC activity. Search handle names for the exact InpOut device
and validated aliases, and retain process identity and lifetime for a match.
An open handle identifies a possible client, not proof of port I/O or a race.

Use the Windows [System Object provider](https://learn.microsoft.com/en-us/windows/win32/etw/system-providers#system-object-provider)
with the locally reviewed Handle keyword `0x2`. Do not add the broad General
keyword that saturated the earlier recording. Preserve the reviewed provider,
event-version and payload-layout checks.

The saved corrected trace establishes a useful new observation path: all
1,107 live File-create records had blank names, but 1,103 of 1,105 File-close
records had names. A close payload's inline name was independently checked
against the original ETL. This makes named transient-client detection
possible on this build; start/end snapshots could miss a client that opens
and closes between them.
Do not rely on an older parser comment claiming live-close names are empty.

## Establish detection once

1. Hold one diagnostic-owned ordinary file open across an immediate-stop
   baseline rundown and each action recording. Verify its name, PID, handle
   and File type in the original payload. Start did not provide handle-start
   rundown in the saved trace, so do not assume a start inventory exists.
2. Inside each action window, open and close a second ordinary file with a
   unique marker name. Require a matching create and named close, using PID,
   handle, object and a consistent in-window lifetime. An ambiguous reuse,
   duplicate, missing event or unsupported layout invalidates that join.
   Do not attach a later name to an older object pointer across reuse.
3. Resolve the specific ACPI detection gap with one bounded offline review
   of the pinned `ACPI.sys` Event 7 emitter: external ordinary/EX method
   evaluation, EC-query dispatch, filters, and root versus nested emission.
   This is a concrete prerequisite for interpreting a missing target event.
   A generic method event or a synthetic parser fixture is insufficient.

Root-only ACPI tracing may suffice for the narrower external-caller
hypothesis if that coverage is established: the copied AML has no caller of
`TBCC`, and `TBCC` is the established AML caller of `ECRR`. That inference
must retain the copied-table/runtime-namespace limit. If coverage cannot be
established, report that path as unmeasured; additional silent recordings do
not improve its detection probability.

Do not directly invoke `TBCC/ECRR` as a positive control. `ECRR` publishes a
mailbox request, while `TBCC` can also change `TYPC` and call `SPNT`. A future
target-device open/close control would likewise require a separately reviewed
fixed CREATE/CLEANUP/CLOSE path and explicit execution scope; an ordinary-file
control does not prove that every InpOut handle will receive a name.

## Three candidate action windows

Perform each applicable round trip once, with roughly 10 seconds before the
first action and 15 seconds after each edge, at most 60 seconds per window.
Keep the existing supported fan settings; no direct-PWM override is active
during these observations.
Record the action and independent state confirmation; a click or elapsed
time alone does not establish that the stimulus occurred.

| Window | Deliberate action | Source basis and confirmation |
|---|---|---|
| Type-C / Thunderbolt | Disconnect and reconnect one already-used, non-storage Type-C peripheral that does not supply AC power, on the relevant port. Keep a separate usable display. | Strongest target hypothesis: `TBCC` logs `TBT` and updates `TYPC`. The known `_Q40 -> UBTC.UFEC -> RECR` path exercises Type-C notification handling, but is **not** an established edge to `TBCC/ECRR`. Record device disappearance/reappearance and any observed `_Q40/UFEC/RECR` events. |
| AC power policy | One AC -> battery -> AC round trip with a charged battery and the user present. | `_Q13` notifies AC0/BAT0 and conditionally IPF participants. Record actual power-source transitions and `_PSR/_BST` or other relevant method activity. No source-proven edge from this action to `TBCC` exists. |
| Thermal profile policy | One supported Silent -> Balanced -> Silent round trip, or capture an already-needed profile round trip. | `_Q30` conditionally notifies IETM and calls `TMSW/WLTC`; `_Q33` calls `TMSW`. A supported profile change is a candidate stimulus, not proof those queries will fire. Confirm each profile through existing typed readback and record the methods actually observed. This is a challenge action, not a required intermediate profile for entering direct control. |

Type-C is skipped if no suitable existing peripheral is available. A skipped
case is untested, not passed. Power/profile actions use normal supported
controls and the established baseline/restoration rules. Do not invent an
AML invocation to force a missing event. No recovered edge currently justifies
adding a CPU/GPU stress benchmark, sleep/resume or a large profile matrix to
this first pass. Add one only for a concrete caller hypothesis or an expanded
intended operating scope.

## Bound the recording and preserve the result

Use one reviewed controller invocation for the agreed batch, unique owned
sessions, fixed provider masks and local ignored outputs. New administrator
recording needs its own execution authorization; the earlier one-run approvals
are exhausted. The user performs physical actions only while recording is
confirmed active. No action is scheduled while a session starts or stops.
If recording ends before the return edge, restore the physical baseline or
confirmed profile through the agreed supported control and mark the window
inconclusive. Trace failure alone must not strand a changed state. Existing
stop-on-ambiguous-hardware-submission rules still apply; never guess or replay
an uncertain profile write.

Retain the 128 MiB System Object cap and 128/512 requested 64 KiB buffers.
Use a short marker-only calibration window first to check volume and controls.
Reserve 32 MiB for up to 512 buffered blocks and another 32 MiB for the
previously approximately 21 MiB end rundown. Stop at an on-disk watermark of
64 MiB or the 60-second ceiling, accounting for larger actual allocations if
Windows reports them. These are collection bounds,
not a guarantee against a sudden burst. Cap exhaustion, any lost events or
buffers, failed stop, or missing control makes that window inconclusive.
If calibration cannot support the planned duration, shorten the agreed action
window before running it; do not silently change masks or drop records.

Stop by the acquired session handle in `finally`, verify that the unique
session is absent, and retain controller/decoder statuses. A sequential
baseline rundown plus action recording has an intervening gap: the baseline
is context, not continuous exclusion. Do not join object lifetimes across it.
All target matches, unnamed File rows, unmatched lifetimes and decoder gaps
remain explicit. Same-rundown pointer/name matching recovered none of the
previous 947 blank File rows; a new batch must not silently discard them.

## Decide before collecting more

| Result | Consequence |
|---|---|
| Target method or named InpOut client observed | The action identifies a possible competing path. Analyze that actor and its timing; an invocation/open alone does not prove a collision. Do not label that case quiet. |
| Valid controls, completed/restored action, zero target observations, and adequate coverage of the claimed paths | Classify the tested paths as observationally quiet. This supports accepting low practical contention concern for the same fixed configuration in a short attended experiment. Record the residual coverage limits explicitly. |
| Loss, failed control, unconfirmed stimulus, unresolved target-tracing coverage or a relevant unattributable handle lifetime | The affected path is inconclusive. Allow one targeted correction that fixes that specific failure; repeating silence is not a correction. |

Unrelated blank File handles do not themselves demonstrate a competitor.
They remain a named-handle coverage limit. If a source-identified candidate
process has a potentially relevant unnamed handle, mark that candidate
inconclusive. Otherwise retain the remaining unnamed population as explicit
unmeasured risk; do not turn it into either a proven blocker or a claim of
complete absence. A decision accepting that risk is distinct from a complete
measurement pass.

Agree on the actions, covered paths, residual risks and decision before the
batch. Do not keep adding repetitions after a clean result to demand absolute
absence. These few correlated challenges provide qualitative evidence; they
do not establish a percentage such as 99.9% or a statistical collision rate.

The resulting empirical decision is scoped to the exact host, firmware,
driver/software set, fixed AC power, fixed peripherals, awake session and
Silent profile. The proposed direct-PWM interval starts only after trigger
actions finish and the intended state is independently confirmed. Any change
to that scope invalidates the qualification. Handling such a change must be
part of the separately reviewed abort plan; ETW is not a synchronous interlock.

This can close the *unobserved competing-client risk assessment* without
claiming a hardware lock. Exact-device admission, request/readback integrity,
bounded exposure and verified Auto restoration still need concrete support
in the separately authorized write procedure. Production ownership contracts
and the disabled fan-stop capability remain unchanged.

## Reviewed source locations

The local copied DSDT defines `TBCC` at file offset `0x36D46`. Its disassembly
places `TBCC` at line 38705, `_Q13` at 38389, `_Q30` at 38513, `_Q33` at 38544
and `_Q40` at 38584; cached SSDT 21 defines `UFEC` at line 272. These are local
analysis line numbers, not runtime addresses. See the pinned provenance in
the [ACPI analysis](so6120-acpi-vendor-transport.md).

The inspected ACPI.sys SHA-256 is
`5de94c7819c690178fef3d656e4f743b2c46bfd58d6fd981e8bdfe7efa3d3022`.
The reused corrected ETL SHA-256 is
`38c97eab33dd66a5b98c0566542ae52fae4e26ff797db87012d3ade02432fe35`.
Raw captures, paths and process identities remain outside Git.
