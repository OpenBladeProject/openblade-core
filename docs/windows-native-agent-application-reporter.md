# Native Agent application reporter

The managed Session Agent reports the foreground application to the Service
(`ApplicationProfileReporter`). The native Agent candidate can do the same when
it is built with `agent-application-reporter-v1`. The feature is off by
default. Its build records the marker `session-application-reporter-v1`, and
both package readers refuse an image that carries the feature or the marker by
name until a packaging stage admits it.

The Agent owns this role because it needs the signed-in user's foreground
window and processes. The Service only receives the reported executable path.

## What the build contains

| Layer | Owns |
| --- | --- |
| `openblade-application-reporter` | Selection, recency, dedup, renewal and sequence policy. No native or IPC calls. |
| `WindowsForegroundWatcher` | WinEvent registration, window and process resolution, retained process handles. |
| `session_native/application/owner.rs` | One dedicated thread that drives the watcher. |
| `session_native/application.rs` | The transport and the role lifetime. |
| `session_native/service_client.rs` | The report exchange with the installed Service. |

The installed monitor hands every verified, fresh status to a profile feed. The
feed keeps the application profiles' selection fields (id, path, activation
mode, enabled) and republishes only a change. A list that cannot be read keeps
the earlier one.

The owner thread applies the latest profiles, then polls the watcher every
100 ms, so a foreground change is reported within about that time. The
watcher's blocking Win32 calls never run on the Agent's runtime, so the other
roles are not delayed. At most one request is in flight. The transport sends it
and returns the outcome to the owner. Only `Stored` and `Applied` acknowledge a
report.

## Matching managed

- A report is sent when the selected path changes, at the five-second renewal
  while a context is selected or a report is unacknowledged, and for a clear
  that was not acknowledged. A lost session cannot send a clear, so it is sent
  when the session is regained.
- The request is `reportApplicationContext` v85 with a fresh request id and
  sequence for every attempt, one instance id for the Agent's lifetime, and an
  explicit `null` path for a clear.
- Each report uses its own connection. A failed or refused report is repeated
  only at the next renewal. There is no backoff or immediate retry.
- A foreground match wins. A `WhileRunningAfterActivation` profile is kept
  while its exact process lives, ordered by the last activation. Reactivating a
  process advances its recency and keeps its retained handle.
- Path matching uses the managed ordinal case-insensitive comparison. A reused
  PID is never taken for the old process.

## Deliberate differences from managed

- Before any byte is sent, and again before the reply is accepted, the Agent
  proves the installed Service generation and the pipe peer, as its status
  subscription does. Managed connects to the pipe name without that proof.
- The foreground watcher is sampled every 100 ms. Managed is event driven.
- A status whose application profile list cannot be read leaves the earlier
  list in place instead of failing the whole status.
- A status with unchanged selection fields is not republished.
- A transport failure is not logged. Managed traces it, and traces a
  `Blocked` result.
- The Service status subscription does not pause or restart reporting. A
  restarted Service learns the context at the next renewal, as with managed.
- A watcher that cannot start, or that retires itself, ends the Agent with a
  failure instead of leaving it silently unreporting.
- The role stops with the other Agent roles. A report that is in flight is
  dropped, which closes its connection, and the owner thread is joined before
  the Agent releases its Origin and status custody.
  A stop that arrives while the hooks are still being installed waits at most
  the five-second startup budget. After that the Agent moves on and the owner
  retires itself when the OS call returns.

## Offline verification

- Profile decoding, the profile feed and the owner loop run with the scripted
  platform watcher. The composition tests run the real owner thread, transport
  and shutdown against it, with an injected report transport. They cover
  ordering, dedup, recency, retries, a refused or failed reply, authority loss,
  handle retirement, and stop during a pending report.
- The report exchange runs against an in-memory Service. It checks the managed
  frame, every result status, peer verification before and after, a stopped
  Service, a silent Service and cancellation.
- Package tests (Rust and Deployment) check that the feature and the marker are
  refused by name.
- CI tests, checks and lints the feature alone and beside the other Agent
  features.

## Runtime validation still needed

None of the following has run:

- the WinEvent registration, window resolution and exit detection on a live
  desktop, including a locked session and a desktop switch
- a report to the installed Service on its real pipe, including peer
  verification and the Service's own session checks
- a Service restart and a Service that is stopped while a report is pending
- the installed Agent's stop with the role running
- the CPU cost of the 100 ms poll on a real system

Physical or attended validation needs separate authorization.
