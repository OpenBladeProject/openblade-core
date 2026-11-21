# Native Agent function row

The managed Session Agent turns the RZ09-0581 function row into media,
brightness and Project keys while the Service confirms keyboard Driver mode
(`WindowsReactiveKeyboardSource` with `FunctionRowKeyboardHookProcessor`). The
native Agent candidate runs the Windows-hook backend of that role when it is
built with `agent-function-row-v1`. The feature is off by default. Its build
records the marker `session-function-row-v1`, and both package readers refuse
an image that carries the feature or the marker by name until a packaging stage
admits it.

This stage observes the Service only. It sends no Service command, so no
admitted action has an effect (see [Effects](#effects)).

AGENTS.md architecture rule 1 still holds: the Service alone owns Driver mode,
and the Agent consumes its confirmed status. Any unknown status passes every key
through.

## What the build contains

| Layer | Owns |
| --- | --- |
| `openblade-domain::function_row` | The hook policy: admission of a new press, latching, repeats, stale releases and missed-edge recovery. |
| `openblade-platform-windows::function_row_hook` | The `WH_KEYBOARD_LL` adapter, its single expiring admission word, the bounded action queue and the action worker. |
| `session_native/function_row/admission.rs` | Verified status to the hook's observation, with expiry and invalidation. |
| `session_native/function_row/preference.rs` | The per-user backend preference. |
| `session_native/function_row/ownership.rs` | The Razer-driver journal guard. |
| `session_native/function_row/host.rs` | The hook and worker threads, their retirement and the proof required before installing again. |
| `session_native/function_row/effects.rs` | The action effects of this stage. |
| `session_native/function_row.rs` | The role lifetime and the admission renewal. |

The installed monitor hands every status update to the admission in the same
status watcher as the other roles, and runs the role as one more supervised
feature.

## Status

The admission reads three status members, all only while `deviceVerified` is
true, as managed `ServiceReactiveLightingLoop.ObserveStatus` does:

- `firmware.modelNumber` must be exactly `RZ09-0581`. Managed also classifies
  RZ09-0528, RZ09-0484, RZ09-0530 and RZ09-0529; this role refuses them, so
  they project to no device and every key passes through.
- `keyboardDriverModeActive` is Active for `true`, Inactive for `false`, and
  Unknown for `null`, an absent member or any other JSON value. Only Active
  lets a new press be intercepted.
- `functionKeyPrimary` is FunctionKeys for `0` and MultimediaKeys for `1`;
  anything else is unknown.

An unverified status makes all three unknown. A reconnect, a failed origin
check or a stale status (older than the two-second status budget) invalidates
the admission at once, before the error ends the status watcher.

## Admission and expiry

The hook reads one packed atomic word holding the observation and its expiry,
and never takes a lock. Each verified status publishes it until the status
stops being fresh. While the role runs, a 500 ms tick re-verifies the Agent's
origin and renews the expiry to one second from now. The status subscription
has no heartbeat: an idle subscription checks its Service generation and pipe
peer every 500 ms and reports a failure as a reconnect, which invalidates the
admission. The renewal stands in for that idle period, and a stalled runtime
lets the admission expire within a second. Stop invalidates the admission
before the hook retires.

## Hook lifetime

The supervisor installs the hook only when all of these hold, rechecked for
each install:

- the per-user preference selects WindowsHook;
- no Razer-driver ownership journal exists (see [Ownership](#ownership));
- the Agent's session is the active console session;
- a fresh, verified status names RZ09-0581.

A later unknown status does not remove the hook; it passes keys through, as the
managed hook stays installed. A preference change or stop retires it. The
admission drops the backend first, so no new press is admitted. Then the hook
drains held keys for at most 250 ms, which stays below the 300 ms
`LowLevelHooksTimeout`, unhooks, and the worker resets. Another hook is
installed only after the previous run retired cleanly
(`HookControl::retired_cleanly`). A run that ends uncleanly or with a native
failure leaves ownership uncertain, and the role never installs again in this
Agent lifetime. A stop waits at most two seconds for the threads.

## Preference

The backend is the `functionRowBackend` member of
`%LOCALAPPDATA%\OpenBlade\preferences.json`, read as managed
`UiPreferenceStore.LoadInputPreferencesAsync` and
`KeyboardInputBackendJsonConverter` read it: a missing file or member is
WindowsHook, the file is bounded at 1 MiB and may start with a BOM, members
match case-insensitively, and a string may be a name or a number, comma-joined
as `Enum.TryParse` allows. A failed read keeps the last value. These choices
differ from managed:

- Before the first successful read the backend is unknown, so no hook is
  installed. Managed starts at WindowsHook.
- Other members are not validated.
- Two members whose names differ only in case fail the read. Managed takes the
  last one.
- The file is polled every second by its length and write time. Managed also
  watches the directory.

## Ownership

Managed `WindowsFunctionRowOwnershipGuard` parses
`function-row-driver-ownership.json` and `snaptap-driver-ownership.json` and
still admits the hook when a journal excludes the function row or comes from an
earlier Windows boot. The native guard is stricter: any journal that exists, or
whose existence cannot be observed, refuses installation. It never reads,
changes or deletes a journal. Ownership is also bounded by:

- the stop-event singleton, which admits one Agent per installed image path in
  the session (managed uses the same name, so a managed Agent at that path
  cannot run beside it);
- the in-process hook slot, which refuses a second hook with `AlreadyOwned`;
- the clean-retirement proof before any reinstall.

## Effects

Managed `FunctionRowActionExecutor` sends media keys and Win+P through
`SendInput`, steps keyboard brightness and holds the display-brightness usage
through Service commands, and falls back to WMI brightness with the Tray OSD.
The native Agent has no media sender, and Service commands are not admitted in
this stage. Every action therefore returns `Unsupported` and records one
diagnostic per action. The display usage never reports `NotSubmitted`, so the
executor selects no WMI route.

A no-op behind a suppressed key would swallow the key. Managed reads Fn from the
Razer HID report through Raw Input (`FunctionKeyObservation`). That observer is
not ported, so Fn stays unknown, the policy admits no press, and these effects
are unreachable in the installed role.

## Managed parity vectors

`tools/openblade-windows-host/tests/data/function-row-hook-vectors.json` is
recorded by `ServiceReactiveLightingLoopTests.NativeFunctionRowVectors`, which
also verifies it:

- `cases` replay `FunctionRowKeyboardHookProcessor` (a hook edge) and
  `FunctionRowHookRecovery` (a Raw Input edge the hook missed). They cover every
  Driver-mode knowledge, backend, Fn state and primary mode, every key,
  RZ09-0528, RZ09-0484 and an unsupported device, latching, stale releases and
  missed edges.
- `status` rows drive `ServiceReactiveLightingLoop` and record what its hook
  reads, beside the status members as the IPC codec writes them, including
  `keyboardDriverModeActive` true, false and null.
- `malformed` rows must be refused by every reader.

The domain test `function_row_vectors` replays the cases, and the Agent test
`function_row::tests::vectors` projects the status rows. Native differs in two
cases, both listed in the domain test:

- `sequence/started-mid-press`: a first edge that Windows already reports as
  down may have reached the application before the hook started, so native
  passes the whole press.
- `fn/unknown`: managed reads an unknown Fn as released; native admits no press.

To regenerate, set `OPENBLADE_REGENERATE_NATIVE_FUNCTION_ROW_VECTORS` to the
data directory and run the managed test.

## Not matched yet

- Fn observation, so no press is admitted.
- Every effect, including the Service commands and the WMI fallback.
- The managed fallback from an absent `functionKeyPrimary` to the active
  profile's laptop behavior. The recorded default profile does not exercise it.
- The function-row status the managed loop publishes to the Tray.
- The Razer-driver backend and its handoff.
- Live Windows validation of the installed hook.
