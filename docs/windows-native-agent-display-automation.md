# Native Agent display automation

The managed Session Agent applies the active power profile's display settings,
and an active application profile's display override, whenever the Service
reports a confirmed power source change or application change
(`ServiceDisplayAutomationLoop` with `DisplayAutomationCoordinator`). The native
Agent candidate runs the same automation when it is built with
`agent-display-automation-v1`. The feature is off by default and implies
`agent-display-profile-apply-v1`. Its build records the marker
`session-display-automation-v1`, and both package readers
(`native_package::capabilities` and `WindowsNativePackage.RequireCapabilities`)
refuse an image that carries the feature or the marker by name until a
packaging stage admits it, because this role writes to the display without a
request.

The role adds no mutation. Every automatic write is the reviewed native
applyProfile composition (`profile_apply::transition`), so each field keeps its
read-before-write, host fence and readback, and the first refused or ambiguous
step ends the profile. Nothing is replayed.

Spans below are against the branch head that added this document. Managed
paths are under `src/`; native paths are under
`tools/openblade-windows-host/src/session_native/`.

## Managed inventory

### Triggers and their sources

| Trigger | Managed source | Coordinator entry |
| --- | --- | --- |
| A Service status (subscription opened or event) | `SharedServiceStatusFeed`, `ServiceDisplayAutomationLoop.cs:35-37` | `Observe` (`DisplayAutomationCoordinator.cs:134-257`) |
| The subscription reconnecting | `ServiceDisplayAutomationLoop.cs:38-40` | `MarkUnavailable` (`:380-387`) |
| The status enumeration failing | `ServiceDisplayAutomationLoop.cs:48-52` | `MarkUnavailable`, then a 1 s delay and a new subscription |
| A manual display mutation starting | `SessionDisplayRequestDispatcher`, wired at `OpenBlade.SessionAgent/Program.cs:234-237` | `BeginManualOperation` (`:259-286`) |
| A manual display mutation finishing | same | `CompleteManualOperation` (`:293-335`) |
| A successful explicit display query | `Program.cs:238-241` | `ResumeAfterSuccessfulExplicitRead` (`:341-374`) |

`Observe` classifies each status (`:144-256`):

- ExternalUnknown or a retained source is unconfirmed:
  `MarkUnavailableCore(manualExternalPower)` (`:147-151`, `:702-722`) cancels
  the active transition and, when a write is in flight, pauses with a
  cancellation hold.
- The first confirmed source (`:168-185`) records it and schedules only when
  an application profile is active and nothing is paused or held.
- The same source (`:187-225`) reschedules a pending transition when the power
  context was lost (`:189-201`). An application change while a write is in
  flight cancels it and pauses with a cancellation hold (`:203-213`). Otherwise
  an application change schedules, unless paused or held (`:215-222`). A power
  profile edit without an application, baseline or earlier target is not
  applied.
- A source change (`:227-255`) cancels the active transition, forgets the
  application baseline and ownership, and schedules the new source's profile
  unless writes are paused. A write in flight becomes a cancellation hold.

`ResolveTarget` (`:750-765`) takes the source's power display settings and the
first enabled application profile whose ID is active.

### Coalescing

There is no timer or queue. `ScheduleTransition` (`:724-739`) cancels the
active transition and starts the latest target under a new generation; every
stage of `RunTransitionAsync` (`:438-692`) checks that generation and its own
cancellation before it records anything. An unchanged target schedules
nothing.

### Writes and their order

1. For an application target without a recorded baseline, one baseline query
   (`:453-481`). An unavailable or failed query records its result and writes
   nothing.
2. An application cannot own a refresh rate its baseline cannot restore
   (`:482-488`). The merge (`:490-491`, `MergeDisplayTarget` `:767-790`) takes
   each field from the application override, else from the baseline the
   application owns, else from the power profile.
3. Admission (`:492-524`) checks the generation, records ownership, marks a
   configured profile in flight with its fields uncertain, and saves the
   write-ahead checkpoint. A failed save latches the pause and writes nothing.
4. `ApplyProfileForTransitionAsync` (`DisplayProfileApplier.cs:168-181`) runs
   under the applier's operation gate (`:418-432`) and writes refresh rate,
   HDR, color profile and brightness in that order, skipping fields that
   already match (`:243-371`, `ProfileMatches` `:434`). `EnsureCurrent`
   (`:397-411`) requires the admitted power context before each step.

### Retries and failure handling

- Two attempts (`:526-544`). Only Failed or Blocked is retried, after a 1 s
  delay that honors cancellation. Indeterminate is never retried.
- Success with an owned but unrestorable refresh rate is Blocked (`:546-553`).
- Indeterminate pauses with a manual hold (`:573-580`) until an explicit read
  confirms every uncertain field (`HasCompleteReadback` `:874-886`).
- A pending manual operation keeps the pause (`:582-588`).
- Otherwise the pause and uncertainty clear, and a cancellation hold resumes
  the latest target on the confirmed source (`:590-632`). A failed save latches
  the pause (`LatchDurabilityFailure` `:868-872`).
- A canceled delay or step (`:635-654`) pauses with a manual hold and records
  no result.
- Any other exception (`:655-680`) records an Indeterminate failure and pauses.

### Reporting

The durable record is `DisplayAutomationRecoveryState`, saved through
`recordRecoveryState` (`TryRecordRecoveryState` `:831-866`) to
`%LOCALAPPDATA%\OpenBlade\display-automation.json` with a trace warning on
failure (`Program.cs:212-233`). `LastTransitionResult` (`:123-132`) and
`LastConfirmedPowerSource` (`:112-121`) are in-memory properties that no
production code reads.

### Lifetime

The loop runs for the Session Agent's lifetime (`Program.cs:384-386`,
started at `:516`, supervised at `:526-566`), independent of the tray. The
coordinator is disposed with the Agent (`DisposeAsync` `:411-436`), which
cancels the active transition and waits for it.

## Native mapping

| Managed | Native |
| --- | --- |
| Loop triggers | `installed_monitor.rs:117-153`: after power admission takes each status, `Automation::observe` (`display_query/automation.rs:140-168`); a reconnect or a failed callback calls `Automation::unavailable` (`:172-175`). |
| `ResolveTarget` | `display_query/automation/target.rs:23-42`, over `Snapshot::display_automation` members decoded by the profile-set codec. A status managed would refuse withdraws power admission and marks the context unavailable. |
| `Observe` | `Recovery::observe` (`display_query/recovery/automatic.rs:223-294`). |
| `MarkUnavailableCore` | `Recovery::mark_unavailable` (`:298-315`). |
| `BeginManualOperation` | `Recovery::begin` (`display_query/recovery.rs:293-319`) with `begin_manual` (`automatic.rs:191-194`). A manual mutation first calls `preempt` (`automatic.rs:218-220`) through `manual_turn` (`display_query/write.rs:157-166`). |
| `CompleteManualOperation` | `Recovery::complete_with` (`recovery.rs:335-371`) with `complete_manual` (`automatic.rs:199-204`). |
| `ResumeAfterSuccessfulExplicitRead` | `Recovery::explicit_read` (`recovery.rs:376-393`) with `resume_latest` (`automatic.rs:208-213`), called from `Writer::query` (`write.rs:247-256`). |
| `ScheduleTransition` and `CancelActiveTransition` | `schedule` (`automatic.rs:560-578`) fills one slot and wakes the runner; `cancel_active` (`:550-555`). |
| `RunTransitionAsync` | `run_transition` (`automation.rs:231-303`) with `plan` (`:318-376`), `transition_baseline` / `transition_unavailable` (`automatic.rs:328-345`), `admit_transition` (`:353-387`), `complete_transition` (`:391-448`), `cancel_transition` (`:451-461`) and `fail_transition` (`:465-486`). |
| `ApplyProfileForTransitionAsync` | `ProfileApplier` (`automation.rs:63-90`) runs `profile_apply::transition` (`display_query/profile_apply.rs:503-539`) under the display operation gate. |
| Applier operation gate | `Gate` (`write.rs:145-151`) serializes every manual query and write with the transition. |
| Task scheduling and disposal | `serve` (`automation.rs:182-227`) runs one transition at a time on a blocking thread and waits only for the slot or stop. Stop cancels the running transition and joins it. `run_automated` (`display_query.rs:543-601`) runs it beside the display endpoint as one lifetime. |
| Retry delay | `Cancel::sleep` (`automatic.rs:63-106`), a condition-variable wait that a cancel ends at once. |
| Durable record | `Recovery::persist` (`recovery.rs:409-440`), the same members and file. |

The queue is one slot, so it is bounded, and scheduling replaces the slot just
as managed replaces the active transition. No loop polls. A transition holds
the recovery lock only between steps, never during a display call.

## Omitted managed behavior

- `recordReconciledPowerSource`: the installed managed Agent never supplies
  it (`Program.cs:218-233`); the checkpoint's `LastReconciledPowerSource`
  carries the source.
- The trace warning when the checkpoint cannot be saved: native has no trace
  sink. The failure stays visible as the latched pause and a Blocked result.
- The 1 s resubscription after a failed status enumeration: the native status
  watcher reconnects on its own and reports Reconnecting. A failed callback
  marks automation unavailable and ends the monitor, which stops every role
  and retires power admission.
- `LastTransitionResult`, `LastConfirmedPowerSource` and `WhenIdleAsync`: no
  production caller reads them. Native keeps the last result in memory for
  tests only.

## Deliberate differences

- A resumption waits for confirmed power. Managed re-admits the latest
  target's source itself when it resumes, even while the status is still
  unconfirmed (`:625-632`). Native schedules only under the watcher's
  confirmed power admission; otherwise the target stays pending and the next
  confirmed status of that source schedules it. This is the one difference in
  the parity vectors.
- Two generation counters. The coordinator's generation and power admission's
  generation advance together at each scheduling point, where managed has one
  counter. Only the values differ.
- The native unresolved-field gate. A transition whose settings touch a field
  with a possibly unresolved native submission is refused at admission
  (Blocked, `profile_apply::PAUSED`), as a manual write would be. Managed has
  no such gate.
- Manual preemption order. A manual mutation cancels the running transition
  before it waits for the display gate, as managed `BeginManualOperation` runs
  before the applier's gate. Native begins the manual operation after the
  canceled transition has recorded its result rather than before. The pause
  and checkpoint end the same.
- The active-console fence. Each native profile step also requires the active
  console session, so a locked or switched session is refused before any
  submission (Blocked) and retried once like any Blocked attempt. Neither lock
  nor unlock is a trigger on either side.
- Unrelated application entries. Native decodes only the active profile's
  display override. Managed's status codec also refuses a malformed entry that
  is not active.
- Fatal errors. A fatal profile step records the status it reached and ends
  the Agent; a fatal baseline query records Indeterminate, pauses and ends the
  Agent. Managed records an Indeterminate failure and continues.

## Managed parity vectors

`tools/openblade-windows-host/tests/data/display-automation-vectors.json` is
recorded by `DisplayAutomationCoordinatorTests.NativeAutomationVectors`
(`tests/OpenBlade.Session.Tests`). Each case drives the real coordinator over a
scripted display: status triggers, manual operations, explicit reads and
display changes in; each step's result and checkpoint, every profile write,
baseline query and retry delay, and the last result out. A write can fire
steps while it holds the display, as a topology change or lock does. The
managed test checks the file holds exactly the reviewed cases and that every
recorded output still matches. Regenerate it with
`OPENBLADE_REGENERATE_NATIVE_DISPLAY_AUTOMATION_VECTORS` set to the data
directory.

The Rust replay (`display_query/automation_vectors.rs`) runs every case
through the native coordinator and runner in the watcher's order. Its
`DIFFERENCES` table names the only case where native records something else,
`mid-apply/lock-resumes-after-unlock`, with the reason above.

## Not matched yet

Native display behavior, packaging admission and installed validation remain
separate gates. Physical validation of automatic display writes has not been
done.
