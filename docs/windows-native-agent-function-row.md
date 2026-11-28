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
  anything else is unknown. Each verified status replaces the last mode, so a
  status without one makes the mode unknown again and keys pass through.
  Managed keeps the last mode it read when neither the status nor its profile
  fallback names one; native's choice is the pass-through direction.

An unverified status makes all three unknown. A reconnect, a failed origin
check or a stale status (older than the two-second status budget) invalidates
the admission at once, before the error ends the status watcher.

## Admission and expiry

The hook reads one packed atomic word holding the observation and its expiry,
and never takes a lock. Each verified status publishes it until that status
stops being fresh, two seconds (the status budget) after it arrived. While the
role runs, a 500 ms tick re-verifies the Agent's origin and extends the expiry
to one second after the tick. After a suspend the tick runs once on resume,
not once for every period missed.

Freshness is therefore a lease on the open subscription, not a limit on the
status's age. The Service sends no heartbeat, so a quiet subscription's last
status may be minutes old and still current; managed assumes the same and
keeps the last status while its subscription stays open. If the runtime
stalls, the renewal stops and the admission expires one second after the last
renewal or two seconds after the last status, whichever is later.

The idle subscription checks its Service generation and pipe peer every
500 ms. A failed read is reported as a reconnect, which invalidates the
admission at once. A changed generation or peer ends the status watcher
instead; the role stops with it, and its stop invalidates the admission. Stop
always invalidates the admission before the supervisor can see it and retire
the hook.

## Hook lifetime

The supervisor installs the hook only when all of these hold, rechecked for
each install:

- the per-user preference selects WindowsHook;
- no Razer-driver ownership journal exists (see [Ownership](#ownership));
- the Agent's session is the active console session;
- a fresh, verified status names RZ09-0581.

A later unknown status does not remove the hook; it passes keys through, as the
managed hook stays installed. Driver mode gates which presses the hook may
admit, not whether it is installed.

The first press of each key after every install passes through. The hook did
not see that key go down, so it treats the first down as a press that may have
started before it and keeps that press's repeats and release paired with it.

A preference change or stop retires the hook. The admission drops the backend
first, so no new press is admitted. Then the hook drains held keys for at most
250 ms, which stays below the 300 ms `LowLevelHooksTimeout`, unhooks, and the
worker resets. Another hook is installed only after the previous run retired
cleanly (`HookControl::retired_cleanly`). A run that ends uncleanly or with a
native failure leaves ownership uncertain, and the role installs no other hook
while it runs (see [Known limits](#known-limits)). A stop waits at most two
seconds for the threads.

## Preference

The backend is the `functionRowBackend` member of
`%LOCALAPPDATA%\OpenBlade\preferences.json`, read as managed
`UiPreferenceStore.LoadInputPreferencesAsync` and
`KeyboardInputBackendJsonConverter` read it: a missing file or member is
WindowsHook, the file is bounded at 1 MiB and may start with a BOM, and members
match case-insensitively. A string is read as `Enum.TryParse(ignoreCase: true)`
reads it: leading white space is skipped, text starting with a digit or sign is
one integer with optional trailing ASCII white space, and anything else is
comma-joined names. A number must be a 32-bit integer. A failed read keeps the
last value. The `preference` vectors pin 68 values. These choices differ from
managed:

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

- the stop-event singleton, which admits one native Agent per installed image
  path in the session;
- the in-process hook slot, which refuses a second hook with `AlreadyOwned`;
- the clean-retirement proof before any reinstall.

The stop event does not exclude a managed Agent. Managed `CreateShutdownEvent`
opens an existing event of the same name without failing, and the managed
Agent's real singleton is its session pipes. A managed Agent can therefore run
its own hook beside a native build whose only role is the function row. That
is harmless while native admits no press. Press admission, a later stage, must
either run with a native role that owns those pipes or add an explicit claim.

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
- `preference` rows hold one `functionRowBackend` value each and the backend
  `UiPreferenceStore` reads from a file holding only that member.
- `malformed` rows must be refused by every reader.

The domain test `function_row_vectors` replays the cases, and the Agent test
`function_row::tests::vectors` projects the status rows and parses the
preference rows. Native differs in two
cases, both listed in the domain test:

- `sequence/started-mid-press`: a first edge that Windows already reports as
  down may have reached the application before the hook started, so native
  passes the whole press.
- `fn/unknown`: managed reads an unknown Fn as released; native admits no press.

To regenerate, set `OPENBLADE_REGENERATE_NATIVE_FUNCTION_ROW_VECTORS` to the
data directory and run the managed test.

## Not matched yet

- Fn observation, so no press is admitted.
- A native Service status feed that carries Driver mode. Only a Service built
  with the unadmitted read feature reads the mode, and only into the status of
  its fresh read job (see
  [Keyboard Driver mode](windows-rust-development-service.md#keyboard-driver-mode-read-only)).
  The unadmitted ownership feature acquires the mode and publishes it the
  same way (see
  [Keyboard Driver mode ownership](windows-rust-development-service.md#keyboard-driver-mode-ownership)).
  The feed reports `null` or no member, so every key passes through.
- Every effect, including the Service commands and the WMI fallback.
- The managed fallback from an absent `functionKeyPrimary` to the active
  profile's laptop behavior. The recorded default profile does not exercise it.
- The function-row status the managed loop publishes to the Tray.
- The Razer-driver backend and its handoff.
- Live Windows validation of the installed hook.

## Known limits

- No permanent retirement latch. A run that retires uncleanly blocks further
  installs only for that supervisor. If a stop exceeds its two-second bound,
  the role returns `TimedOut` and abandons the supervisor thread, which ends
  when its OS calls return. A later role run starts a new supervisor; the
  in-process hook slot still refuses a second hook while the first holds it.
- Idle cost. While the role runs, it re-verifies the origin every 500 ms and the
  supervisor wakes every 100 ms and checks the preference file's length and
  write time every second, even when no hook is installed.
