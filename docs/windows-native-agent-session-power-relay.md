# Native Agent session power relay

The managed Session Agent tells the Service about each suspend and resume of
the user's session (`SessionPowerTransitionRelay`, registered by
`WindowsSessionPowerNotificationRegistration`). The native Agent candidate
runs the client side of that relay when it is built with
`agent-session-power-relay-v1`. The feature is off by default. Its build
records the marker `session-power-relay-v1`, and both package readers
(`native_package::capabilities` and `WindowsNativePackage.RequireCapabilities`)
refuse an image that carries the feature or the marker by name until a
packaging stage admits it beside a native Service that serves
`reportSessionPowerTransition`. No native Service serves it yet.

Managed paths are under `src/OpenBlade.SessionAgent/`; native paths are under
`tools/openblade-windows-host/src/session_native/`.

## Managed inventory

### Registration

`PowerRegisterSuspendResumeNotification` with `DEVICE_NOTIFY_CALLBACK`
(`WindowsSessionPowerNotificationRegistration.cs:146`). A failed registration
is traced and the Agent runs without the relay (`:28-46`). Dispose
disposes the relay first, then unregisters; a failed unregistration
keeps the callback rooted for the rest of the process (`:72-102`). The
callback never unwinds into Windows and always returns 0 (`:104-132`).

### Transitions

`OnPowerNotification` (`SessionPowerTransitionRelay.cs:48-110`) maps type 4 to
suspend and types 7 and 18 to resume and ignores every other type and a
repeated direction. Each transition is a new request: protocol 91, a new
request id, the relay instance and its start (QPC at construction), the next
sequence, the QPC timestamp and the kind.

One report is active and only the latest transition waits behind it. A new
transition replaces the pending one and cancels the active report (`:63-85`). Each transition has 1.5 s
(`SessionPowerTransition.MaximumAgeMilliseconds`) from its timestamp, measured
on QPC (`Remaining`, `:236-245`):

- A suspend callback waits for its report for the rest of that budget, then
  expires it (`:104-110`).
- A resume callback returns at once; a deadline task expires the transition
  (`:94-100`, `:112-127`).
- Expiry drops a pending transition and cancels an active report
  (`:129-146`).
- The budget and cancellation are checked again before each send (`:206-213`).

The sink is `ServiceClient.ReportSessionPowerTransitionAsync`, an ordinary
mutation exchange; its result is only traced. Nothing is replayed. The managed
callback also resets the special keys' function input
(`WindowsSessionPowerNotificationRegistration.cs:112`).

### Codec

`IpcMessageCodec` (`src/OpenBlade.Contracts/IpcMessageCodec.cs:94`,
`:384-388`) refuses an empty request or instance id, a start or sequence at
or before zero, and a timestamp before the start, before writing any byte.

## Native mapping

| Managed | Native |
| --- | --- |
| `SessionPowerTransitionRelay` state and budget | `power_relay::Relay` |
| `OnPowerNotification` | `Relay::on_notification`, called by the registered callback |
| `SendAsync` and `ReportOneAsync` | `power_relay::serve`, on the relay's own transport thread |
| Registration | `power_relay/windows.rs` (`Registration`) |
| Request and reply | `power_relay/wire.rs` |
| `ServiceClient` exchange | `service_client::ReportChannel::exchange`, shared with the application reporter |
| Result trace | `Event::SessionPowerRelay` in the Agent diagnostics |

## Deviations

- The callback hands the report to the transport thread instead of starting
  the pipe attempt on the callback. The transport thread is idle, so the
  attempt starts at once.
- Every exchange first proves the installed Service generation and the pipe
  peer, as each native Agent request does, and again before the reply is
  accepted. The generation is acquired when the role starts, so the first
  report does not spend its budget re-reading the installed package. Before
  each send the transport also proves the Agent's installed origin; a failure
  ends the role.
- Because those checks can themselves take time, the exchange checks the
  report once more after the pipe peer verifies and immediately before the
  first byte is written. A report whose budget ran out, or that a newer
  transition superseded, is abandoned there without a write. The write and
  the reply wait then share one deadline, the end of the budget, and a
  report still waiting at that deadline is abandoned, not failed.
- A report ends in the relay before its result is recorded in the
  diagnostics, where managed traces the result first, and so does the report
  that stop drops. A waiting suspend callback is released, and the pending
  transition becomes active, without waiting for the diagnostics write. Each
  report still records exactly one result.
- The registration functions are resolved at run time from the System32
  powrprof.dll, through the loader shared with power-mode automation, rather
  than imported at load time. A Windows build without them fails the
  registration, and the role runs without the relay, as after any refused
  registration. Once a registration succeeds, powrprof.dll stays loaded for
  the rest of the process, because Windows calls back through it.
- Windows holds a small slot as the callback context, allocated per
  registration and never freed, so a callback reaches it however late it
  runs. The slot holds the relay while registered; a callback clones the
  relay out of it and works on its clone. A successful unregistration empties
  the slot, so a later callback does nothing, and a callback still running
  keeps its own clone alive. After a failed unregistration the relay stays in
  the slot, as managed roots its callback.
- The request id is generated when the report is sent rather than at the
  notification, so the callback does no fallible work. It is still fresh for
  every request and never reused.
- The resume deadline is the transport's bound on the report rather than a
  separate task. A pending resume past its budget is skipped when it becomes
  active, as managed skips it before the send.
- The special keys' input reset is not ported; special keys are not part of
  this stage.

## Parity vectors

`tests/OpenBlade.SessionAgent.Tests/SessionPowerTransitionRelayNativeVectorTests.cs`
records, in `tools/openblade-windows-host/tests/data/session-power-relay-vectors.json`,
the exact payload the managed codec writes for fixed requests, the requests it
refuses, and the requests the real managed relay sends for notification
sequences (repeated directions, unrelated types, both resume types). Set
`OPENBLADE_REGENERATE_NATIVE_SESSION_POWER_RELAY_VECTORS` to that directory to
regenerate it; otherwise the test verifies the file. `power_relay/vectors.rs`
replays every case through the native encoder and relay. Native differs in no
case.

## Not validated

Nothing here has registered a real callback or reached a real Service. The
native Service side is not merged. End-to-end validation needs that Service
and an attended sleep and resume, approved by the user immediately
beforehand.
