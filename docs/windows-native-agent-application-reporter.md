# Native Agent application reporter

The managed Session Agent reports the foreground application to the Service
(`ApplicationProfileReporter`). The native Agent candidate can do the same when
it is built with `agent-application-reporter-v1`. The feature is off by
default. Its build records the marker `session-application-reporter-v1`, and
both package readers refuse an image that carries the feature or the marker by
name until a packaging stage admits it.

The Agent owns this role because it needs the signed-in user's foreground
window and processes. The Service only receives the reported executable path.

## Packaging rule for the stage that admits it

`reportApplicationContext` is served only by a Service built with
`service-thermal-mutation-v1` (see `StatusRequest::serves`). Without it the
Service answers the report as unserved and the Agent's reports never take
effect. The stage that admits the reporter must pair it with a Service that has
that feature, and must refuse a reporter Agent that is paired with a Service
without it. The readers do not check this today because they refuse the
reporter outright.

## What the build contains

| Layer | Owns |
| --- | --- |
| `openblade-application-reporter` | Selection, recency, dedup, renewal and sequence policy. No native or IPC calls. |
| `WindowsForegroundWatcher` | WinEvent registration, window and process resolution, retained process handles. |
| `session_native/application/owner.rs` | One dedicated thread that drives the watcher. |
| `session_native/application.rs` | The transport thread, the role lifetime and the status wiring. |
| `session_native/service_client.rs` (`ReportChannel`) | The report exchange with the installed Service. |

The installed monitor hands every verified, fresh status to a profile feed. The
feed keeps the application profiles' selection fields (id, path, activation
mode, enabled) and republishes only a change. A list that cannot be read keeps
the earlier one.

The owner thread applies the latest profiles and polls the watcher. The
watcher's hook callback wakes it after every foreground or desktop notice, and
completions, profile changes and stop wake it too. A 500 ms housekeeping tick
covers what has no notice: a process exit, a lock without a desktop event, and
the five-second renewal. The hook thread's own wait is 250 ms, which only bounds
how long a cancellation can go unnoticed. At most one request is in flight.

The Service transport runs on its own thread and runtime, apart from the
Agent's shared one. Establishing a Service generation re-reads and hashes the
installed package and verifying it queries the SCM, so those blocking calls
cannot delay the display, GPU or battery roles. The transport holds one
verified generation across reports and acquires another only when it is retired
or a verification fails. What still runs on the shared runtime is the status
subscription and decoding the profile list of each status. Only `Stored` and
`Applied` acknowledge a report.

## Matching managed

- A report is sent when the selected path changes, at the five-second renewal
  while a context is selected or a report is unacknowledged, and for a clear
  that was not acknowledged.
- The request is `reportApplicationContext` v91 with a fresh request id and
  sequence for every attempt, one instance id for the Agent's lifetime, and an
  explicit `null` path for a clear.
- Each report uses its own connection, and the connect is bounded at two
  seconds. A failed or refused report is repeated only at the next renewal.
  There is no backoff or immediate retry.
- A foreground match wins. A `WhileRunningAfterActivation` profile is kept
  while its exact process lives, ordered by the last activation. Reactivating a
  process advances its recency and keeps its retained handle.
- A lock, a secure-desktop switch or another user on the console leaves the
  retained processes and the renewals alone. Only the foreground becomes
  unknown, so a Foreground profile ends and a retained process stays reported.
  A different session id forgets every process, as defence in depth: the active
  session is always the Agent's own, so no real session reaches that branch.
  The Service accepts reports from the locked console session.
- Path matching uses the managed ordinal case-insensitive comparison. A reused
  PID is never taken for the old process.
- A failure of any selected Agent role ends the Agent, as the managed
  supervisor's `WhenAny` does. Like managed, the Agent then finishes draining the
  other roles, releases its Origin and custody, reports its diagnostic and ends
  with a fail-fast exception, so Windows Error Reporting sees a crash. A panic
  takes the same path, except that the roles polled by the panicking future are
  dropped rather than drained. These are ordinary exits that Windows does not
  restart: an admission denial, an origin retired under the Agent, and any
  failure that surfaces while draining after a deliberate stop, which is
  reported. These exit 0 quietly, as in managed: a deliberate stop, and another
  Agent that already owns the stop event or a feature pipe (the Tray also starts
  the managed Agent).
- The installed Agent registers for Windows Application Restart with flags 0, for
  every role, and unregisters on a deliberate stop. Windows is expected to
  restart an Agent that crashed or hung after running at least 60 seconds, and
  after an update or reboot, to which the 60 second rule does not apply. Whether
  crash restarts need user consent, and whether Windows restarts after the
  fail-fast, is expected behaviour pending attended validation. Managed registers
  `--application-restart`. Native Origin admission accepts no arguments, so it
  registers no command line (NULL) and the restarted Agent starts with none.
  Like managed, a crash loop is bounded only by Windows' own restart budget.
- The installed Agent is a GUI-subsystem executable, as the managed Agent is a
  WinExe, so a sign-in task or a restart opens no console window.
- Windows cannot give a restarted process the sign-in action's working directory,
  so the Agent no longer requires it. Origin has already proved the image,
  package, process and session, and nothing before that point uses a relative
  path or searches the working directory for DLLs. The Agent sets its working
  directory to the verified install directory instead.

## Deliberate differences from managed

- Before any byte is sent, and again before the reply is accepted, the Agent
  proves the installed Service generation and the pipe peer, as its status
  subscription does. Managed connects to the pipe name without that proof.
- The reply and the write are each bounded at two seconds. Managed waits for the
  reply without a limit, which would hold the one in-flight report, and every
  renewal, behind a wedged Service.
- The foreground is resolved from notices and a 500 ms tick, not events alone.
  Only the latest window per poll is resolved, so a window that changes twice
  between two polls (A, B, A) never activates B. Polls follow notices within
  milliseconds, which makes that rare. Managed resolves every notice.
- An elevated `WhileRunning` application is retained through a
  `QUERY_LIMITED_INFORMATION | SYNCHRONIZE` handle, which managed cannot open
  for it. Managed does not retain such an application.
- A status whose application profile list cannot be read keeps the earlier list
  instead of failing the whole status.
- A status with unchanged selection fields is not republished.
- A transport failure is not logged. Managed traces it, and traces a
  `Blocked` result.
- The Service status subscription does not pause or restart reporting. A
  restarted Service learns the context at the next renewal, as with managed.
- A watcher that cannot start, or that retires itself, ends the Agent.
- The role stops with the other Agent roles. A report that is in flight is
  dropped, which closes its connection, and the owner thread is joined before
  the Agent releases its Origin and status custody. A stop that arrives while
  the hooks are still being installed waits at most the five-second startup
  budget. After that the Agent moves on and the owner retires itself when the
  OS call returns.
- The private v91 fixture in the development host still refuses reports while
  its session is inactive. It does not model the managed Service's rule above.

## Offline verification

- Profile decoding, the profile feed, the status wiring and the owner loop run
  with the scripted platform watcher. The composition tests run the real owner
  thread, transport and shutdown against it, with an injected report transport.
  They cover ordering, dedup, recency, retries, a refused or failed reply, lock,
  desktop and session changes, handle retirement, wake-driven latency, and stop
  during a pending report or a stuck startup.
- The report exchange runs against an in-memory Service. It checks the managed
  frame, every result status, peer verification before and after, generation
  reuse and re-acquisition, a connect that never completes, a silent Service and
  cancellation.
- The restart registration runs through a seam that records its calls.
- Package tests (Rust and Deployment) check that the feature and the marker are
  refused by name.
- CI tests the reporter lane, checks the non-test Agent, which is the only
  build that compiles the monitor wiring, with the reporter alone and beside
  the other Agent features, and lints the same lanes.

## Runtime validation still needed

None of the following has run:

- the WinEvent registration, window resolution and exit detection on a live
  desktop, including a locked session, a secure desktop and a desktop switch
- a report to the installed Service on its real pipe, including peer
  verification and the Service's own session checks
- a Service restart and a Service that is stopped while a report is pending
- the installed Agent's stop with the role running, and Windows Application
  Restart after a crash
- the CPU and wakeup cost of the owner and hook threads on a real system

Attended validation of the restart, which needs separate authorization:

- a crash of an Agent that ran at least 60 seconds restarts it (also after the
  fail-fast, and with or without user consent), the restarted
  Agent passes Origin admission with no arguments and a different working
  directory, and it serves again
- a crash before 60 seconds does not restart it
- a deliberate stop does not restart it

Physical or attended validation needs separate authorization.
