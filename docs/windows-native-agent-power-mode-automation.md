# Native Agent power-mode automation

The managed Session Agent sets the current user's Windows power mode for the
confirmed performance preset, separately for AC and battery, whenever the
Service reports a status (`ServicePowerModeAutomationLoop` with
`WindowsPowerModeAutomationCoordinator`). The native Agent candidate runs the
same automation when it is built with `agent-power-mode-automation-v1`. The
feature is off by default. Its build records the marker
`session-power-mode-automation-v1`, and both package readers
(`native_package::capabilities` and `WindowsNativePackage.RequireCapabilities`)
refuse an image that carries the feature or the marker by name until a
packaging stage admits it, because this role changes a Windows setting without
a request.

Managed paths are under `src/`; native paths are under
`tools/openblade-windows-host/src/session_native/`.

## Managed inventory

### Triggers

| Trigger | Managed source | Coordinator entry |
| --- | --- | --- |
| A Service status (subscription opened or event) | `ServicePowerModeAutomationLoop.cs:36` | `Observe` (`WindowsPowerModeAutomationCoordinator.cs:71-122`) |
| The subscription reconnecting | `ServicePowerModeAutomationLoop.cs:39` | `MarkUnavailable` (`:124-131`) |
| The status enumeration failing | `ServicePowerModeAutomationLoop.cs:48-51` | `MarkUnavailable`, then a 1 s delay and a new subscription |
| Agent shutdown | `Program.cs:389-400` (`await using`) | `DisposeAsync`: cancels and awaits every apply |

`Observe` (`:81-91`) ignores a status, and forgets the last target, unless
all of these hold: control access is Active, the device is verified, the
observation is not a retained power source, Performance is not an
unconfirmed write, the active source is PluggedIn, Battery or UsbC, and the
confirmed state reports a performance preset. FixedMaximum reports no preset,
so its association stays stored until a preset is confirmed again.

The target is the supply (UsbC and PluggedIn are both AC), the preset and the
profile set's association for that preset. An unchanged target does nothing.
A changed one cancels the active apply; an absent association leaves Windows
unchanged; otherwise a new generation schedules one apply.

### Apply and retry

`RunApplyAsync` (`:180-245`) makes at most two attempts with a 1 s delay after
a failed one. A result is published only while its generation is current and
not canceled, and the Agent traces failures (`Program.cs:391-398`). The
controller (`OpenBlade.SessionAgent/WindowsPowerModeController.cs`) reads the
user-configured AC or DC mode, returns when it already matches, sets it and
reads it back; a different readback is NotRetained. Cancellation is checked
before the read and before the set, and missing powrprof entry points fail
the apply (`:86`).

### Restoration

Managed never restores or reverts a Windows power mode. Agent exit, a profile
change and an unknown or unconfirmed Service status only cancel the active
apply and forget the last target, so the next confirmed status applies again.

## Native mapping

| Managed | Native |
| --- | --- |
| `Observe`, `MarkUnavailable` | `power_mode::Automation::observe`, `unavailable`; the status is decoded by `power_mode/status.rs` from the v91 status the Agent's subscription already verified |
| Loop wiring | `power_mode::publish_status`, called by `installed_monitor` for each status update, as the other status roles are |
| `RunApplyAsync` | `power_mode::serve` and `run_one`: one runner owned by the feature lifetime |
| `WindowsPowerModeController` | `power_mode/windows.rs` (`UserPowerModes`), behind the `Supplier` port |
| Result trace | `Event::WindowsPowerMode` in the Agent diagnostics, failures only |

The association list is decoded by the domain profile-set decoder
(`openblade_domain::profile_set::decode::windows_power_modes`). The status
decode refuses a status the managed codec would refuse for these members, and
any per-profile legacy `windowsPowerMode` value other than null. As in
managed, an absent `unconfirmedWrites` list reads as empty and an explicit
null is refused. The last list and its decoding are kept, so an unchanged
list is compared rather than decoded again for every status.

## Deviations

- Managed runs an apply inside `Observe` when the controller completes
  synchronously, as the real one does. Native runs every apply on one runner
  task, in order, so a status update never waits for Windows. A newer target
  still cancels the older apply before it reaches Windows; one already inside
  a Windows call finishes it and its result is not published.
- Because of that runner, two statuses that arrive before the runner next
  runs can end differently. Managed applies the first status's target inside
  its own `Observe` and then the second's. Native may cancel the first apply
  before it runs, so only the second target reaches Windows. When the second
  status leaves Windows unchanged (a cleared association) or is unconfirmed,
  Windows keeps the mode it had before both, where managed would have kept the
  first target's mode. The vectors observe each status after the runner is
  idle, so they do not exercise this.
- Managed binds status members case-insensitively. The native status reader,
  like every native status reader, reads only the camelCase names the Service
  writes, so a differently cased `ObservationReason` or `UnconfirmedWrites`
  reads as absent here.
- The powrprof functions are resolved from the System32 copy at each apply,
  through the loader shared with the session power relay, so a Windows build
  without them fails the apply, as managed's lazy binding does, without
  stopping the Agent from loading.
- Before each Windows call the runner proves the Agent's installed origin
  again; a failure ends the role. Stop cancels the running apply and waits for
  its Windows call, as managed `DisposeAsync` does.

## Parity vectors

`tests/OpenBlade.Session.Tests/WindowsPowerModeAutomationNativeVectorTests.cs`
drives the real managed coordinator through 29 cases (first observations,
unchanged and changed targets, association edits, unavailable and
unconfirmed statuses, and retries with a status arriving during the delay)
and records the results, Windows calls, each retry delay in milliseconds and
the last result in
`tools/openblade-windows-host/tests/data/power-mode-automation-vectors.json`.
Set `OPENBLADE_REGENERATE_NATIVE_POWER_MODE_VECTORS` to that directory to
regenerate it; otherwise the test verifies the file. `power_mode/vectors.rs`
replays every case through the native decode, automation and runner. Native
differs in no case.

## Not validated

Nothing here has run against the real powrprof functions or a real Service.
Physical validation needs an attended session: read and save the user's AC
and DC modes, let one confirmed status apply a different mode, confirm it by
readback, and restore the saved modes.
