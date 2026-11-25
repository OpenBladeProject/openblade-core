# Windows Rust development service

The development host serves synthetic status over a local Windows named pipe. The existing WinUI `ServiceClient` can connect by name through its existing constructor. The tool is a standalone Cargo workspace outside the product's Rust dependency graph and publish inputs. The installed C# service remains the hardware owner.

For the feature-gated native SCM lifecycle and the LocalSystem managed client, use the [Phase 5j disposable Windows VM runbook](windows-rust-phase5j-vm.md) under separate VM authorization.

## Package the ordinary fixture roles offline

With PowerShell 7.4 or later, give the development script the directory
containing the two ordinary executables pinned by the Phase 7e signed scratch
Setup test. The pins at the Phase 9a base identify artifact revision
`41a3237e81188ed0780e89a2ca2d170da7d61db0`. Use the ordinary pair from that
build. The separately built simulated GPU-close Agent and VM SCM host have
different bytes and are outside this package.

```powershell
$candidate = './build/development-deployment/RustFixtureCandidate.ps1'
$receipt = & $candidate -FixtureDirectory $ordinaryRoles -Package ./rust-fixture-candidate.zip
& $candidate @receipt
```

Run the opt-in package and production-boundary checks against that same pair
with `./tests/Build.Tests/Test-RustFixtureCandidate.ps1 -FixtureDirectory $ordinaryRoles`.

The ZIP has exactly two native x64 executables, a fixture manifest, and its
P-256 signature. The script generates a fresh in-memory key for each package
and returns the public key and complete ZIP SHA-256 in the receipt. Keep the
receipt separately through a trusted local path for later verification.
Verification checks the full ZIP against that receipt, then checks the exact
entry inventory, signature, reviewed per-role hashes, and native PE headers.
The key authenticates this fixture package relative to the receipt. The
manifest declares `installable: false` and carries no release or Setup identity.

| Candidate role | Fixture executable | Installed compatibility target |
| --- | --- | --- |
| Service | `openblade-windows-host.exe` | `OpenBlade.Service.exe` at the existing automatic LocalSystem SCM service `OpenBlade`, with no fixture arguments |
| Session Agent | `openblade-rust-session-agent.exe` | `OpenBlade.SessionAgent.exe` at the existing `OpenBlade Session Agent` least-privilege logon task, with the install directory as its working directory and no fixture arguments |

The installed targets describe a future compatibility gate. The current
Service fixture uses private foreground startup, the SCM executable is gated
to the disposable VM fixture, and the current Agent requires private fixture
startup. A separately reviewed native pair must support the installed entry
points and existing IPC contracts before changing payload admission. That
work needs protected staging and independent installed hash, image path,
PID, account, and session readback, followed by typed Service and Session
Agent readiness tied to those processes. Verify stop, restart, sign-in,
and rollback to the previous bytes and registrations before admitting an
installed cutover. Production Publish and Setup continue to use the managed
roles and their dependency and runtime files.

## Compile the installed Session Agent launch boundary

The Windows host workspace also exposes the standalone
`openblade-windows-agent-candidate` binary behind
`native-agent-entry-compile-check`. It accepts the managed sign-in action
with no arguments and requires independent evidence for the installed image,
authorized interactive logon, and managed endpoint ownership. Those bindings
are absent, so startup fails closed. The development
`openblade-rust-session-agent` binary still requires an explicit fixture
command when the candidate feature is enabled.

The two installed-role candidates are compile-only. Their binary entry points
have separate module trees from the fixture roles. Neither is a Setup payload.

### Read-only Service status binding

After the other adapter checks pass, the candidate lease must start its
retained Service watcher and obtain a fresh initial subscription snapshot.
The separate one-shot client uses the shared Service v90
`GetStatus` request and response on the local `OpenBlade.Service.v2` endpoint.
The exchange is limited to a 64 KiB frame and a two-second deadline, including
connection establishment. Only listener absence or a busy pipe is retried
before submission. Cancellation or failure closes the attempt and a subsequent
read starts with a new request ID. The lease rejects an initial snapshot
retained for two seconds or more. Service revision is scoped to the producer,
so receipt time is used for freshness.

The client requires independently admitted, still-live installed Service
process evidence and checks its PID and session zero on the connected handle
before sending, after the response, and before delivering each event on the
retained connection. The proof provider and installed lease remain unwired.
Status fields retain the producer's meaning, including inactive or unsupported
states. Feature records are preserved for future consumers.

The native watcher sends a fresh v90 `SubscribeStatus` request on each
connection and delivers an open marker only after a successful correlated
response with a snapshot. It reads subsequent typed `statusEvent` frames in
wire order. A new open always establishes a new baseline, including after a
producer restart with a lower revision. The Service can suppress unchanged or
live-reading-only publications and rotate an idle subscription after its
15-minute lease. The client applies the two-second budget to opening and
validating the initial response. Event reads have the same frame size bound
and can wait for data or cancellation while idle.

After a transport disconnect the watcher drops the old connection and reopens
immediately. Failed opens emit a reconnect marker with no snapshot, and retry
delays double from 250 ms up to five seconds. A verified open resets the delay.
Malformed protocol frames and failed peer proof end the watcher. Cancellation
releases the owned connection, including during a partial frame. The Rust
watcher delivers updates directly to one consumer. The managed shared fan-out
and its per-consumer coalescing remain in the managed Agent.

Managed Agent call sites use the following Service operations:

- `GetStatus` supplies the cached status used by `Blade18FnActionExecutor` and
  `SpecialKeyActionExecutor`.
- `SubscribeStatus` supplies an initial correlated snapshot and subsequent
  `statusEvent` frames. `SharedServiceStatusFeed` fans out the latest update
  to display, power mode, lighting, mute and THX consumers. Input and LampArray
  observation also consume status. The managed client immediately attempts to
  reopen after a dropped stream, then publishes reconnecting on a retryable open
  failure and backs off from 250 ms to five seconds. Consumers invalidate
  actionable state during reconnect. Function-row interception requires fresh
  confirmed Driver-mode metadata.
- `ObserveState` refreshes confirmed performance after a failed shortcut in
  `ServiceReactiveLightingLoop`. It requests no live-reading sensors and has
  its own five-second cancellation window. It is distinct from cached status.
- The remaining typed requests report application context and power transitions,
  manage native brightness usage and keyboard brightness steps, apply one-shot
  laptop controls and performance shortcuts/previews, and drive lighting
  overrides, pulses, rendered frames, suppression, and the playback mute
  indicator.

The managed server's protocol-51 compatibility path is limited to cached
`GetStatus` and `SubscribeStatus` with a read-only projection. The native
Agent status binding requires v90, matching the ordinary managed Agent. The
installed Service candidate has a v90 `SubscribeStatus` handler behind the
existing listener admission. It returns a correlated failure when the
publication feed is unavailable. The startup probe
still requires the exact self-issued `GetStatus` exchange.

The Agent candidate also has a one-shot v90 `ObserveState` client for
performance shortcut readback. Its request matches
`ObserveStateAsync(LiveReadingSensors.None)`. The Service candidate requires
an active caller and a fresh performance read from a separate admitted port.
The installed metadata observer has no firmware read port, so it returns a
correlated refusal. Optional live sensor selections also receive a refusal.
The Agent requires a Service generation admitted for fresh observation. The
v90 status envelope alone carries no proof of a fresh firmware read. The
Agent also checks verified device state, control access, power classification,
observation state, Fan Stop ownership and unresolved performance writes.

The private `firmware-performance-compile-check` adapter implements the
reviewed RZ09-0581 thermal getter. It reuses the exact registry and HID
selection, then verifies the retained overlapped handle as
`1532:02E0`, usage `0001:0002`, with a 91-byte feature report. HID
`SET_FEATURE` submits the `0D/82` getter for each of the two channels.
After the managed 30 ms settling interval, HID `GET_FEATURE` reads the
corresponding reply. Both replies must pass report ID, length, checksum,
transaction, command, status and channel validation and agree on one reviewed
thermal state. The installed mailbox read path also decodes ordinary manual,
battery and HyperBoost selectors. Custom reads use the shared typed `0D/87`
codec: CPU `01 01 00`, then GPU `01 02 00`, with distinct transaction IDs.
Both three-byte responses must confirm their selector and a Low, Medium or
High level. Boost is rejected. Automatic High/High rejects the observation;
Manual High/High confirms Custom/Maximum with unknown levels, preserving the
managed Automatic handback interpretation. It never publishes High/High levels
or carries earlier levels into that confirmed unknown result. RPM, targets,
GPU offsets and live sensors remain unobserved.

The legacy guarded adapter retains its full-adapter and active-session
requirements. The installed mailbox reader instead uses the existing typed
read custody, independently of write capabilities, power resolution or a
nonzero UI session. Both recheck their applicable identity/custody proof before
traffic and publication. The allocator reserves pairs across leases in the
process, stopping after 127 reservations without wrapping. Standard reads use
one pair; Custom reserves a second pair only after its selectors agree.
At most 63 complete Custom observations fit, and cancellation can burn a
reservation. Exhaustion submits no further command and cannot renew the
retained timestamp; presentation expires at five seconds. Sustained reads
remain a required full-backend item.

Phase 10m runs the getter on one owned OS thread per accepted observation.
Discovery, open, ConfigMgr registration, same-handle verification, submission,
completion polling, cancellation, and callback drain all run on that thread.
The Service refuses concurrent observations without queueing them. It releases
the status-source lock before waiting, so GetStatus and subscription refresh
can proceed. The request deadline or disconnect revokes delivery. The worker
rechecks authority and rejects a reply that completes after its deadline.

The managed getter invokes GPU after successful CPU decoding without a gap
cancellation check. Its shared gate can return QueuedCanceled without a native
submission or port poison; a claimed Canceled result poisons the port even if
the inner session remains Healthy. The native worker owns an entire claimed
observation and its private Reader, with no component queue or reusable port
exposed to callers. GPU invocation performs fresh pre-submit admission; any
canceled claimed observation seals that Reader and retires it before returning.
A concurrent observation cannot submit. Incomplete reads publish no levels.

The worker releases its process-wide fence only after terminal I/O, callback
drain, and handle closure. A blocked call retains the lease and fence even
when the caller has returned. Shutdown stops admission and requests cancellation.
A successful drain joins the worker and confirms cleanup. If the shutdown
budget expires, the Service can report only that cancellation was requested
and retirement is unconfirmed. An explicit cleanup failure retains the
resources and quarantines the native slot until process exit.

The typed result can populate a fresh v90 reply only with independently
verified status guards for the same live authority epoch. The installed read
projection carries the complete decoded preset/fan state,
HyperBoost observation and nullable Custom settings in the existing v90 schema.
Accepted levels use numeric CPU/GPU values and maximumFans=false; the manual
selector does not imply that separate setting. Retained ordinary status has no
fresh-read witness.
The installed loaded-generation and sole-owner proofs are still unavailable.
The metadata observer therefore continues to refuse firmware observation.
Offline fixtures block native-call substitutes and feed the pinned report
shape through the real reader and in-memory candidate dispatcher.

#### Keyboard Driver mode (read-only)

A Service built with `service-keyboard-driver-mode-read-v1` also reports the
RZ09-0581 keyboard device mode as managed `keyboardDriverModeActive`. The
feature implies `service-mailbox-observation-v1` and adds the capability marker
`service-keyboard-driver-mode-read-v1`. Both package readers refuse the feature
and the marker by name, so no admitted image sends the query. The transaction
pool that blocked it is now reclaimed (see below); admitting the feature is a
separate decision. A build without the feature sends no `00/84` query and publishes no `keyboardDriverModeActive`
member, as before.

The read sends only the reviewed `00/84:0000` query from the exact-device
semantic admission (`Rz09_0581_02e0_3_01_LightingSemanticAdmission.json`,
`deviceMode`). The reviewed `00/04` setters exist only behind the protocol
crate's `device-mode-setters` feature, which only the ownership feature below
enables, and in its tests. `RazerReport::create` and `encode_report` remain public encoders, so
this is not a type guarantee. The host's `device_mode_guard_tests` are a
textual allowlist over non-test source in the Service's crates: they fail on a
file other than the codec and the write recipe that names the setter, a file
other than the owner that builds a Driver-mode job, a file that renames an
encoder on import, or a change in the count
of encoder calls. They do not see an encoder reached through a glob import or a
macro. The read alone never acquires, maintains or releases Driver mode; see
the ownership section below.

The value is the confirmed firmware mode, not ownership, matching managed
status: `true` for a `0300` reply, `false` for `0000`, and `null` when the reply
is missing, fails validation or names another mode. A status for an unverified
device is `null`. Managed reads once at startup and then every 15 seconds. The
native Service adds no loop: the query follows the thermal getter in the same
serialized, freshly admitted read job, once the last result is 15 seconds old.
Until then, each fresh read under the same admission publishes the cached
result without reading it again. One instant per job decides the query and
selects the published value. A result is bound to the read admission that
produced it, so a lifecycle, custody or generation change makes it unknown
until a new job reads it.

The query has its own one-second budget and never takes the last 500 ms of the
job, which leave time for retirement and the final admission check. A failed,
unanswered or timed-out query seals that Reader like any incomplete exchange
and publishes `null` until the next interval. So does a job that was asked to
query and returned nothing, including one the worker refused to start; this
fails closed. Neither is sent again before the interval ends. A job with too
little time left sends no query and stays due.

Retirement has no deadline of its own, because the worker owns the native
resources until terminal I/O and callback drain are confirmed; this is
unchanged from the thermal read alone. A reading whose retirement ends after
the job deadline is discarded. So a stalled query whose canceled GET retires
more than about 500 ms late costs the thermal read of that job, as a stalled
thermal getter already does.

Each query reserves one more pair of transaction IDs, on a job that already
reserves one (two for Custom). It sends the first ID, and retirement frees the
unsent second. An answered ID returns to the pool once a later request with
the same command is answered, so while their exchanges are answered, read
jobs, with or without the query, keep at most one answered ID per command out
of the pool and never exhaust it at any interval (`transaction_tests`). A read
whose exchange keeps failing quarantines one ID each time: about 253 failed
reads exhaust the pool (about 173 after a start whose journal seeds the full
80 IDs, see below), 8.5 minutes at the 2 s subscription poll or 21 minutes
at 5 s. Before reclamation the pool ran out after
about 15 minutes 45 seconds of one job every 15 seconds. The packaging refusal
is unchanged.

The read-only composition publishes its status feed unverified, and in the
mutation compositions a feed status verified after a mutation carries `null`,
because the mutation's own read sends no query. Only the fresh read-job status
carries a mode, so the native Agent still passes every function-row key through.
The vectors in
`tools/openblade-windows-host/tests/data/keyboard-driver-mode-vectors.json`
pin the codec and the published value against managed. Their recorder is
`ServiceControlRuntimeTests.NativeKeyboardDriverModeVectors`. Managed
`RazerReport.ValidateResponse` calls the same Rust codec, so the envelope-failure
rows compare Rust with itself; only the payload decode and the status rows are
independent managed evidence.

#### Keyboard Driver mode ownership

A Service built with `service-keyboard-driver-mode-v1` acquires and maintains
RZ09-0581 keyboard Driver mode and restores Normal at a confirmed suspend and
at shutdown. The feature
implies `service-keyboard-driver-mode-read-v1`, `service-fan-mutation-v2` (the
fan guardian, and the fan handback the shutdown release runs before) and the protocol crate's
`device-mode-setters`, and adds the capability marker
`service-keyboard-driver-mode-v1`. It does not imply profile triggers. Both
package readers refuse the feature and the marker by name, before the read and
trigger features built with it, so no admitted image sends `00/04`. Packaging
admission also waits for the attended checks listed below.

The setters are the reviewed `00/04:0000` and `00/04:0300` of the same
semantic admission. Only the write recipe `Reader::apply_device_mode`
(`discovery/hid_lease/performance/device_mode.rs`) builds them, and only the
owner (`installed/driver_mode_owner.rs`) builds a job for it. The guard tests
allow exactly those files, and the codec, to name the setter or build a job,
and pin that only this feature enables `device-mode-setters`.

Acquire and maintain follow managed `ProductionLightingPort.MaintainDriverModeAsync`
(`src/OpenBlade.Protocol/ProductionLightingPort.cs:855-900`):

- One mutation job reads `00/84` first. A keyboard already in Driver mode is
  reported `true` with no write and no claim.
- From Normal, ownership is claimed before the setter can be submitted, as
  the durable `AcquireIntent` described below. A refusal before submission
  (managed `KnownNotApplied`) reverts the claim; any later failure keeps it.
- The job has a 2 s budget from its own start, after the lane wait. The
  setter is sent only when that budget still covers its acknowledgement and
  the readback, each charged at the baseline exchange's measured length
  (`mutation::Pace::covers`), and the claim's record write is charged too.
  Too little time is known not applied, before any claim.
- The setter's reply must correlate and echo `0300` byte for byte
  (`DeviceModeSet::acknowledge`), and an independent `00/84` readback on its
  own transaction ID must read Driver.
- A setter that may have been submitted without both confirmations is
  Indeterminate. That step publishes `null`, runs no recovery and never
  replays the write. A power or session notice that moves the lifecycle
  epoch between the acknowledgement and the readback is one way to get
  there: the capture's fence refuses the readback.
- Each later due step resolves an Indeterminate acquire read-only. One job
  under a fresh capture, with the step's journal and fan skip conditions,
  reads `00/84` and writes nothing; every checkpoint of that read checks the
  capture's epoch again. Driver means the acquire took effect: the claim it
  made before submission is kept unless a release has since ended it, and
  the read never makes one. Normal means it did not take effect or was
  undone: the claim ends, `false` is published, and the next tick acquires
  through the ordinary claim-before-write job as a new write on new IDs. An
  unreadable or fenced read stays Indeterminate and publishes `null`; one
  refused before any exchange records nothing. Each such step reads at most
  once and never writes in the same tick. Managed has no Indeterminate
  state: it publishes `null`, and its one recovery on a fresh port reads
  `00/84` and may write Driver again within the same step
  (`ServiceControlRuntime.cs:12967-13066`). Native defers that write to the
  next tick, after a confirmed read of Normal. The shutdown and low-power
  releases run as usual while the acquire is Indeterminate.
- A step that fails gets one recovery per failure streak: a fresh capture and
  read-before-write, never a replay. A confirmed mode or a lost device resets
  the latch (`ServiceControlRuntime.cs:12568-12665`).
- A step refused before any firmware command (a refused capture, admission,
  lane or activation, or a write port that failed to open) records nothing,
  spends no recovery and keeps the last confirmed status. In particular it
  never publishes `null` over the read job's result or stops the read job
  from querying.

The step runs on the 500 ms origin tick after the power-source check and
before the lifecycle reconciliation, the managed step order
(`ServicePeriodicObservationLoop.cs:42-90`). It is taken under the source lock
and awaited after it is released. It is due at the first eligible tick and
then 15 seconds after the last step, managed's interval. That first tick is
the managed startup call (`ServiceStartupCoordinator.cs:85-86`) and always
reads `00/84`. After it, a step starts no job when a client's ObserveState
read job confirmed Driver under the current read admission within the
interval, since managed would read and find Driver. Subscription refreshes do
not run the read job, so otherwise a due step on a verified device runs one
mutation job that reads `00/84` first. That is the cost of the managed cadence:
while a client keeps the device verified and no client read recorded Driver
within the interval, every 15 s brings one `00/84` exchange, with a reader
stop and restart and their fresh admissions, when the keyboard is already in
Driver mode. With no ObserveState in the last 5 s the device is unverified and
a due step starts no job. A step failure continues the
loop, and only fatal admission evidence stops the Service. A tick that finds a
request holding the mutation gate tries again at the next tick. A
recovery-only read lease never activates a write, so the step does not run
there at all.

The step writes nothing, and publishes nothing new, while the low-power gate is
set, while this pair's laptop-control journal row is unsettled (managed
`RequiresReconciliation`) and while a fan job or fan row is unsettled (managed
`FanStopController.BlocksOrdinaryHardware`). In the native Service the fan row
is the same per-user journal, so the fan check guards a fan job that has not
joined; the step then does not wait for the lane. Without a fresh
exact-device read (`ReadSource::driver_mode_verified`) the step publishes
`null`, writes nothing, resets the recovery latch (managed
`TryGetVerifiedDevice` false) and stays due.

Write admission is a deliberate deviation from managed. Each job captures the
console user's ordinary write admission, the same candidate the lifecycle tick
uses: fresh exact-device, competing-controller, AC line, session, SID and
lifecycle-epoch checks, and the epoch refuses in low power. With nobody signed
in, with the session locked or switched, or with a competing controller
present, the step is refused before any firmware access. It keeps the last
confirmed status, consumes no recovery and runs again at the next step.
Managed maintains Driver mode with no user signed in. Native adds no
Service-scoped acquire authority for it, and the function row has no user to
serve then. A release never uses this capture; it holds the Service's own
cleanup admission below.

The step publishes through the read cadence that every fresh status projects
(`ReadSource::record_driver_mode`): a confirmed mode, or `null` after a failed
or Indeterminate step. It never edits the subscription feed, so an unchanged
status is never republished. The feed stays unverified as described above.

Ownership is durable, a deliberate deviation from managed, which keeps
`ownsDriverMode` and `driverModeReleaseUncertain` in memory
(`ProductionLightingPort.cs:68`). A restart, a crash or a handback must not
lose an owned claim or a write that may have applied unconfirmed: Setup's
handback assumes every firmware write is journaled, and a doubt must stop
writes across processes too. The record is its own file in the original
owner custody, `owner\keyboard-driver-mode.bin`
(`openblade_domain::driver_mode_history`): `Clear`, `Owned`, `AcquireIntent`
or `ReleaseIntent`, an intent's prior settled state, and the four IDs of the
job that wrote it. Only this process's own Active owner, once its write lease
has activated, replaces it, by compare-and-swap along the domain's edges
(`Store::driver_mode_record_replace`). The owner's flags are derived from the
record its ledger holds (`driver_mode_ledger`) and change only after a
replace returned `Ok`, so memory never runs ahead of the file.

- Each job that may write claims before its setter can be sent. An acquire
  whose baseline reads Normal writes an `AcquireIntent` under the custody's
  ordinary admission. A release whose baseline reads Driver writes a
  `ReleaseIntent` under its boundary's cleanup scope, Owned Stop or the
  suspend generation. A release that finds an acquire in doubt first records
  what its own baseline read, `AcquireIntent -> Owned`, so an intent never
  follows an intent. A claim that cannot be written sends no setter; a
  release then reports `Unrecorded`.
- After the job the owner settles the claim. Confirmed, an acquire becomes
  `Owned` and a release `Clear`; known not submitted, the intent reverts to
  its prior; otherwise it stays in doubt. A release settles only while its
  share still has room for one record write, and so does `Owned -> Clear`
  after a release reads Normal. Otherwise the record keeps what it holds for
  a later read to resolve.
- Before it claims, the job's pace charges a 25 ms floor for each of the
  claim's record writes, never a measured replace: a refused job writes
  nothing, so a slow sample charged there could never be replaced and would
  refuse every later claim. Once the claim has returned, the pace is checked
  again with its real time spent. When the setter, its acknowledgement and
  the readback no longer fit, no setter is sent, the claim reverts as known
  not submitted, and a release reports `Exhausted`, or `Unadmitted` when
  its admission has ended. A release's settle is charged at the longer of
  the floor and the last measured replace, which is its own claim's when it
  made one. The floor is an estimate pending an attended timing on
  RZ09-0581. The keyboard's share is unchanged.
- Any failed record write closes the record for the process, and nothing
  reopens it: after an error the file may hold the old record or the new one.
  A record that cannot be read or is refused (corrupt, another version, a
  leftover stage) closes it at load and is kept byte for byte; startup does
  not fail. A closed owner runs no acquire, maintain or release write, and
  the status projects confirmed reads only. A settle whose custody admission
  is refused before anything is written leaves the record, and its doubt, as
  they are without closing it.
- An intent is resolved read-only, never replayed. An intent a later process
  loads is resolved by that process's first due step with one `00/84` read
  and no `00/04` (`resolve_indeterminate`), and that job also activates the
  write lease the record write needs. Driver records `Owned` for an acquire
  intent and the prior for a release intent; Normal records `Clear`, and any
  acquire waits for the next tick, on fresh IDs. A release doubt this process
  left is resolved from the read cadence, as below. While a release intent
  stands, every release reports `Uncertain` and sends nothing.

Setup refuses to retire the custody or hand back while an intent record is
present, and keeps its bytes; an `Owned` record is only reported. Letting
managed resolve an intent at handback, a v2 handback item with a managed
read-before-write, is a separate follow-up because it changes the managed
Service and needs a versioned header. Until then an intent left by the last
native Stop blocks retirement until a native Service resolves it. Setup also
refuses an unreadable or unknown record and a leftover record stage, and it
refuses to install any build over a record or its stage, because no packaged
build can open the owner custody over them. See "Keyboard Driver-mode record
in Setup" in `windows-rust-thermal-history.md`.

##### Cleanup admission

Every release and every Service fan handback at Stop holds the Service's own
cleanup admission (`thermal_mutation::Cleanup`, built by
`CleanupGuard::capture`), captured fresh for its boundary. Fan mutation
composes it for the owned-Stop handback without the keyboard owner
(`service-fan-mutation-v2`); the low-power scope needs the keyboard owner. It names no console session, SID or logon:
Windows logs every user off before a system shutdown reaches services, and a
suspend may start from the lock screen. Managed releases with nobody signed in
too: `ReleaseDriverModeCoreAsync` checks the fan guardian, a pending
reconciliation, controller presence and its detection warning, then the device
provider's `FreshLightingCleanupAdmission` (exact-device probe and owned
renderer access), and never a user (`ServiceControlRuntime.cs:13398-13520`,
`WindowsVerifiedBladeDeviceProvider.cs:398-420`). Each authorization checks,
fresh, in this order (`thermal_native::admit_cleanup`, which the test model
shares):

- origin and package trust for its scope: owned-Stop trust for Stop and
  shutdown, ordinary trust for a suspend generation;
- the lifecycle fence its scope pinned (`Lifecycle::fence_for`), with origin
  commitment, before and after the Windows checks.
  `CleanupScope::OwnedStop` pins the ordinary epoch, so the gate refuses it in
  low power and any later power or session notice ends it.
  `CleanupScope::LowPower` pins its suspend generation only, as managed's
  `IsActiveForGeneration` does: a lock or unlock during the suspend leaves it
  admitted, and a resume, a DisplayOn or another suspend ends it;
- a known Windows AC line status, then fresh competing-controller absence
  (a typed denial at capture, as the console admission reports it);
- the exact original mailbox custody and generation through
  `ReadCustody::authorize_cleanup`, which names the admitted device. A fan
  handback also needs the owner's own custody generation.

Its authority is the release's `00/04:0000` and the owned fan handback, and
nothing else. `Cleanup` is not a `Fresh` or a `Settle`, so no request,
reconciliation, acquire or settle permit can hold it. Exactly two permits do:
`DriverModePermit::release`, whose job writes Normal only after a fresh
`00/84` read of Driver, and `FanHandbackPermit`, which journals only the
owner's cleanup stages (31, 40-43, 50 and 51) for its exact retained record
and refuses every request stage, preparation, resolution and no-op
confirmation. The lane runs either job only through
`ReadSource::mutate_cleanup_in`, which requires the permit's admission to
equal the custody's cleanup admission of that scope, and each journal row is
written under that same scope (`ReadCustody::journal_replace_cleanup`).

##### Shutdown release

At shutdown the Service releases Driver mode before the fan handback, the
managed order (`ServiceControlRuntime.cs:12925-12950`,
`ServiceControlRuntime.FanStop.cs:430-490`). The release follows
`ReleaseDriverModeCoreAsync("shutdown")` and
`TryRestoreOwnedDeviceModeCoreAsync` (`ProductionLightingPort.cs:1237-1285`):

- It joins active work first (`ReadSource::drain_active`), as the fan handback
  does.
- The managed skip reasons the native Service can observe leave the mode
  unchanged: an unsettled fan row, an unsettled journal row, and no cleanup
  admission. The journal row is read from the latest capture's store, of any
  user (`Runtime::captured_store`): the captures are serialized and every
  store recovers the one custody journal's last row when it opens, so that
  store has seen every row. The last covers a competing
  controller, unknown Windows power, lost origin or custody trust, low power
  and a write lease that never activated
  (`ServiceControlRuntime.cs:13399-13520`). After origin loss the release is
  refused.
- The fan handback tries the owner's own capture first, then the Service's
  cleanup admission with `FanHandbackPermit`, which runs the owner's full
  cleanup recipe with nobody signed in, and only then the settle permit
  (`Fresh::capture_settle`), which reads and appends 51 on Automatic. A job
  that registered may already have sent the owner's reserved cleanup IDs,
  its baseline read's among them, so no later admission of the same Stop
  runs the recipe again. Before row 31 the recipe is paced once against what
  is left of the source drain (`StatusSource::drain_by`: 2 s, or 2.9 s with
  the keyboard share, from the drain's start), with each cleanup job also
  bounded by its own 3 s. Every exchange left is charged as `Pace` charges
  any recipe, and every row left at the second longest exchange; when they
  do not fit it ends with no row and no setter. A job that ends after 31 or
  40-43 leaves a tail that cannot be closed in the process (51 follows only
  30), so Stop reports the handback unconfirmed. A restarted Service finds
  that row unsettled and refuses; nothing settles it yet. That predates the
  low-power slice.
- A fresh `00/84` read of Driver writes `00/04:0000` once, whoever set it, as
  managed does, and requires the exact acknowledgement and an independent
  `00/84` readback of Normal, after its `ReleaseIntent` is written. A read of
  Normal ends an owned claim as a foreign change and sends no setter. An
  acquire intent read Normal is resolved `Clear`, as its read-only
  resolution is, and reported as not Driver: its setter never took effect.
- Each boundary releases at most once: the shutdown, and each suspend
  generation. A job whose measured pace could not fit the setter, its
  acknowledgement and the readback in its time sends nothing and reports
  `Exhausted`; a lane refusal before the job registered keeps the attempt.
- A release that may have submitted Normal without confirming it
  (`Submitted` or `Acknowledged`) leaves its `ReleaseIntent` on disk. It
  stops every later release, acquire and maintain write until a confirmed
  `00/84` read under the current read admission, by a job that started after
  that release, shows the mode (`ReadSource::driver_mode_confirmed_since`).
  Driver records the intent's prior, the claim there was before the release,
  and any other mode records `Clear`; the next due step then acquires through
  the normal cadence. Until then later releases report `Uncertain` and send
  nothing, in this process and in a later one, which resolves the intent with
  its own read-only step. The uncertain write is never replayed.
- A release job whose caller was dropped, as by a Stop during the low-power
  release, stays registered. The next release joins it with the active work
  and reads how far it got before it decides anything.
- A process whose read lease is recovery-only cannot write.

Managed retries the keyboard release once after the fan handback when the fan
guardian blocked it and the handback cleared that block
(`ServiceControlRuntime.FanStop.cs:469-483`). Native has no such retry: the
release already joined every fan job, and the handback refuses, and never
settles, a fan row that is still pending, so nothing can clear the block.

The keyboard has a 0.9 s share of the drain from the release's start, and
the source drain timeout grows by the same share so the fan handback keeps
its own 2 s. Inside the share, the gate wait and the join of active work have
150 ms, and the job has 700 ms from its own start, ending at least 50 ms
before the share does so the lane can retire it. The share also ends the
release when the job's worker has not retired by then, and the fan handback
joins that worker within its own time. By count, a healthy release takes
about 0.28 s besides its three exchanges, if each of its 20 cleanup
authorizations (counted in the test rig) takes 8-9 ms, plus three 30 ms
settles. The per-authorization cost is not measured on RZ09-0581, so that is
an estimate pending an attended timing.
Twice the per-exchange allowance plus those admissions is
about 1.09 s, which does not fit; the job's measured pace refuses the setter
instead when it cannot finish. Native accepts SERVICE_CONTROL_SHUTDOWN, not
PRESHUTDOWN, so at system shutdown the whole Stop must end within
WaitToKillServiceTimeout, about 5 s, as managed's 5 s shutdown cleanup does.
The listener retirement takes up to 2 s, so the Stop drain takes at most
2 + 2.9 = 4.9 s, inside that window and the 5 s SCM wait hint
(`scm_host.rs`). The keyboard gets what the fans leave. These figures are
estimates; a real release on RZ09-0581 still needs an attended timing. A
keyboard outcome (`Release`) is a diagnostic: a refusal, a
skip or an unconfirmed write never skips the fan step or fails the Stop, and
only fatal admission evidence does. An authorization only refuses, so a
refused owned-Stop authorization is checked again for that evidence, such as
lost custody integrity, before it is reported as `Unadmitted`. The Service
has no diagnostics sink yet, so the outcome is dropped at process exit, as an
unregistration failure is.

The record's claim names the job's baseline, setter, readback and spare IDs
before the setter is sent. A settle or a read-only resolution keeps them,
and a revert, whose job sent no setter, returns to the IDs of the record the
claim replaced, so the newest `00/04` reply belongs to the IDs on disk. The
exception is an intent whose setter was never sent and that was not
reverted: a claim whose write failed after it landed, a release whose share
had no room for the revert, a revert its writer's admission refused (a
suspend that refused an acquire's setter also ends the ordinary admission
its revert holds), or a process that ended between the claim and its setter
or revert. It names IDs that sent no `00/04`, so the newest reply, the
earlier record's, is not seeded, and a later setter that read it would
still need its independent `00/84` readback to confirm. The seed is exact
for `00/04` only: a revert also drops the reverted job's answered baseline
`00/84` ID. That reply names the mode the job read, which its unsent setter
did not change, read-only `00/84` IDs are not seeded either, and a `00/84`
read never confirms a write on its own. The store admits a replace by the
domain's edges and compare-and-swap alone, so keeping a settle or a
resolution on the intent's IDs, and building a revert from the replaced
record, is the ledger's job. The next process quarantines the record's IDs in its transaction pool
beside the journal's (`transaction_seed`); managed restarts its IDs at 1
per port. No ID is pinned: the job reserves
both pairs before its first send, and the pool skips the seeded ones. A sent
ID stays quarantined until a correlated reply is read, and retirement frees
the unsent one, so long runs keep at most one answered ID per command out of
the pool (`driver_mode_owner` tests). A stale reply still fails closed: the
acknowledgement must match the request's ID, class, command, status and
checksum and echo the arguments byte for byte, and the independent readback
uses a different ID and must read the target.

A write port that fails to open is known not applied for every mutation job
(`windows::mutation_worker`): the lane and owned Stop keep working.

##### Low-power release

A confirmed suspend runs managed `PrepareForLowPowerCoreAsync` as
`ServiceSessionPowerTransitionCoordinator` does
(`ServiceControlRuntime.cs:12993`): once per suspend generation, within
1.5 s of the notice's QPC timestamp (`SessionPowerTransition.MaximumAgeMilliseconds`,
`low_power_cleanup::MAXIMUM_AGE`), the keyboard release under
`CleanupScope::LowPower` for that generation. A new suspend generation wakes
the origin tick at once (`Lifecycle::suspended`), so the 500 ms interval does
not spend the window. A notice older than the window prepares nothing. Saved
intent is unchanged: the next active reconciliation or request applies it.

- The fans are not touched. AGENTS.md rule 1 names only the keyboard's
  Normal release at a low-power boundary, and managed restores fans there
  only for a Battery Saver fan stop it owns (`OwnsBatterySaverFanStop`,
  `ServiceControlRuntime.cs:13069-13087`, property at `:21049`). Native has
  no Fan Stop guardian yet (`service-fan-stop-v1` serves the requests but
  never stops a fan; `openblade_domain::profile_set::policy`), so owned
  manual fans stay as OpenBlade applied them, no reading after the resume is
  an external change, and owned Stop still hands them back. When Fan Stop
  lands, restore an owned Battery Saver fan stop here, after the keyboard and
  under the same generation's cleanup admission, as managed does. Managed's
  restore has no Windows-AC-only pre-check; do not copy the one owned Stop's
  handback recipe needs (`fan_mutation.rs`). An earlier revision handed
  owned manual fans back at suspend too (the D1 suspend fan handback);
  commit `874b7c07` removed it, which is managed parity. On Fast Startup,
  too, the fans are not handed back at suspend, and owned fans continue in
  the resumed process.
- The keyboard gets the rest of the window, at most its 0.9 s shutdown
  share. Unlike the shutdown release's 150 ms entry, it waits on the gate and
  the join of active work for that whole share, as managed's
  `ReleaseDriverModeAsync` waits on its operation gate
  (`ServiceControlRuntime.cs:13318-13327`), so a request in flight at the
  boundary does not cost the release: the request is refused at its next
  authorization, because the suspend fenced its epoch.
- A lock or unlock during the release does not end it (see Cleanup
  admission), so it neither loses the release nor leaves it in doubt.
- In low power the 15 s step reads and writes nothing, so nothing is
  reacquired until Windows reports the system active again (a resume, or a
  DisplayOn that clears the gate); the cadence then reacquires.
- Fast Startup hibernates the system session instead of stopping services,
  so its PBT_APMSUSPEND runs this path, not the shutdown release. Windows
  logs users off first, which the cleanup admission does not depend on.
  After the hybrid boot the same process resumes, so an in-flight release
  job and a release left in doubt survive it.

The console-identity half of packaging blocker 2 is resolved: every release
holds the Service's cleanup admission, which names no console session, SID
or logon. Both Windows orders are pinned with nobody signed in: logoff and
console-disconnect notices, then PBT_APMSUSPEND with late session notices
during the release, restores Normal under the suspend generation's scope
(`a_fast_startup_order_releases_with_nobody_signed_in`), and logoff notices
before SHUTDOWN leave the Owned Stop release admitted
(`a_shutdown_after_logoff_releases`). Neither takes a console capture.

The preparation's healthy release has three exchanges and 20 cleanup
authorizations, counted in the test rig. Each authorization reads the
Windows AC line status and competing-controller presence; their cost on
RZ09-0581 is not measured, so the preparation's time is unknown until an
attended timing. For comparison, the owned fan handback recipe counted 60
authorizations with its nine exchanges and six journal writes when it still
ran at suspend.

The SCM control callback that delivered the suspend is held until the
preparation has recorded an outcome for that generation, and never past the
notice's QPC stamp plus `MAXIMUM_AGE` (`origin_lifetime::suspend_hold`).
Windows suspends once the callback returns, so without the hold the release
raced the suspend. This deviates from the managed Service, whose callback
returns at once (`WindowsServiceLifecycleEventSource.cs:678-689`), and
matches the managed Session Agent relay, which waits for the Service the
same way (`SessionPowerTransitionRelay.cs:101-108`). The native Agent serves
no `reportSessionPowerTransition`, so before the hold native held nothing on
any suspend, and managed holds nothing on Fast Startup, where no Agent runs.

- Only `SERVICE_CONTROL_POWEREVENT` with PBT_APMSUSPEND that starts a new
  suspend generation waits, and only while the preparation has armed the
  hold (`Runtime::native`). STOP, SHUTDOWN, SESSIONCHANGE, a resume, a
  power-setting read and a repeated or stale suspend return at once.
- Every exit of the preparation records its generation through a drop
  guard: a release with any outcome, a notice too old to prepare, a
  generation already prepared, fatal admission evidence and a preparation
  dropped mid-release.
- The callback runs on the SCM dispatcher thread, and the preparation on the
  runtime that the ServiceMain thread runs (`scm_host::registered_service`),
  so the wait cannot block the work it waits for. A wait on the preparer's
  own thread returns at once. The wait holds neither the ingress slot's lock
  nor the lifecycle's.
- The hold changes no write admission, fence or budget and sends nothing.
  The SCM serializes controls, so a STOP that arrives during the hold is
  delivered after it, at most 1.5 s later.
- A preparation still running when the cap ends goes on; a system that
  sleeps first ends the work at its next authorization after resume.

Known limits, pinned by tests and left for the attended checks below:

- G3, an unactivated write lease. Only a console-captured mutation
  activates this process's write lease (`ReadSource::mutate`), and the record
  custody writes nothing before it. A process in which nobody has signed in
  since it started, such as a shutdown from the logon screen after a cold
  boot, therefore refuses the shutdown release over a loaded `Owned` record
  or a keyboard left in Driver mode: `Unadmitted`, nothing sent, and the
  record stays `Owned` and open, which Setup admits
  (`an_unactivated_process_refuses_the_shutdown_release`). Managed releases a
  fresh Driver read there, whoever set it. A Service-scoped activation bound
  to the loaded record would widen owner custody at the shutdown boundary;
  it waits for V6, whether Driver mode survives a cold boot.
- G4, the Owned Stop fence. Owned Stop pins the ordinary epoch, so a session
  or power notice that arrives during the 0.9 s shutdown release ends it at
  its next authorization. Notices before SHUTDOWN do not, because the
  release captures after them. Whether late logoff notices reach the Service
  during SHUTDOWN is not known; a session-tolerant fence waits for the V3
  trace.
- A shutdown while the gate is still set, after a missed resume, refuses the
  release with nothing sent, as managed blocks its Stop cleanup in low power
  (`a_shutdown_while_suspended_refuses_the_release`).
- A Fast Startup release left in doubt (`ReleaseIntent`) is not resolved
  after the hybrid boot while nobody is signed in. After this process's own
  release, only a confirmed `00/84` read by a client's ObserveState read job
  can resolve it, and no client runs at the logon screen. The first client
  read after sign-in resolves it, and that settle needs no console user
  (`a_fast_startup_release_intent_waits_for_a_client_read`). Until then
  every release reports `Uncertain` and sends nothing, Setup refuses
  retirement and handback, and a full shutdown leaves the intent for the
  next process, whose first console-captured step resolves it read-only.

Packaging blocker 2, the Driver-mode release at shutdown and Fast Startup
with no console user, is code-complete: the cleanup admission resolves the
identity half and the suspend hold the timing half. Remaining before
packaging admission, attended on RZ09-0581:

- V2: a Fast Startup shutdown with Driver mode owned. PBT_APMSUSPEND
  reaches the native Service, the release lands within the hold, the record
  is `Clear` after the hybrid boot and the keyboard reads Normal at the
  logon screen, with no reacquire before sign-in.
- V3: a full shutdown and a restart. The release and the fan handback land
  within WaitToKillServiceTimeout, the record is `Clear`, and the session
  notices seen during the release are recorded (G4).
- V4: a shutdown from the logon screen once after a sign-in and sign-out in
  the same boot (`Restored` expected) and once with no sign-in since boot
  (`Unadmitted` expected, G3).
- An attended timing of the Driver-mode record write against its 25 ms
  floor, and physical validation.

#### Live readings (read-only)

A Service built with `service-live-readings-v1` serves an ObserveState that asks
for live readings instead of refusing it. The feature implies
`service-mailbox-observation-v1` and adds the capability marker
`service-live-readings-v1`. Both package readers refuse the feature and the
marker by name. A build without the feature still refuses the request with
"Live readings are unavailable."

- **Fan speeds.** The read job sends the two 0D/88 getters of the exact
  RZ09-0581 capture (`rz09-0581-pid-02e0-bios-3.01-fan-rpm-fixture.json`,
  `productionWired`) after the thermal getters, only for an ObserveState that
  asks for `FanSpeeds`. They use the same serialized job, read custody,
  checkpoints and settle as the thermal read, with their own 1 s budget and
  never the job's last 500 ms. Both selectors must decode, as managed
  requires. A speed above the managed codec's 20,000 RPM bound counts as a
  failed read.
- **Fan speed budget and back-off.** No 0D/88 SET is sent unless the remaining
  budget covers that exchange's SET, settle and GET (130 ms: 50 ms for each
  transfer and the 30 ms settle), and the pair starts only when the budget
  covers both. A pair that cannot start sends nothing and stays due. A pair
  whose first SET went out and that confirmed nothing pauses the pair: no
  job sends it for 15 s, as the Driver-mode query waits, doubling per
  consecutive failure to 5 minutes. A confirmed pair ends the pause. A job
  that sent no SET, such as one whose thermal read failed, whose worker did
  not start or that lacked the time, leaves the pair due and the pause
  unchanged. The worker counts the pair as it submits the first SET, so a
  pair sent by an observation that a disconnecting client dropped still
  pauses the next one.
  Each unconfirmed pair quarantines at most one transaction ID (an answered
  first query's ID is reclaimed and an unsent second ID stays free), and the
  pool of about 253 IDs is shared with every thermal read and mutation.
  Without the pause, a pair that never answers would exhaust it in about
  8.5 minutes at the 2 s poll. With it, the first hour costs about 16 IDs
  and each later hour 12, so the pool lasts about 21 hours, as long as the
  thermal read's own exchanges are answered.
- **Fan speed retention.** Managed publishes null after a failed pair. Native
  keeps the last confirmed pair, under the same read admission, through two
  consecutive jobs that confirmed none (failed, paused or unable to start)
  within the 5 s retention window, then publishes null. A pair seconds old is
  still the last confirmed speed, and blanking it for one transient miss at
  the 2 s poll would show a failure rather than the laptop (rule 4). A
  retained pair is that earlier confirmation, never a fresh one: it never
  outlives the window or crosses another admission, and every mutation
  drops it, since a write may change the fans.
- **Battery power flow.** `CallNtPowerInformation(SystemBatteryState)`, only for
  an ObserveState that asks for `BatteryPower`, mapped as managed
  `MapBatteryPowerFlow`. A failure is null.
- **Battery health.** The battery class IOCTLs, summed as managed
  `MapBatteryHealth`, published as `batteryHealth` in every status of a
  Service that has a thermal reader. The first status captures it. While it is
  null, the first verified observation and each power-source change capture it
  again. A captured value is never cleared. A Service whose reader did not start
  (`MailboxSource::NoReader`) publishes diagnostic status only and no
  `batteryHealth`, where managed captures it at construction whatever the
  device.
- **Firmware versions.** Managed's `FirmwareVersions`, published as `firmware`
  in every status of a Service that has a thermal reader, every ObserveState
  reply and the subscription feed, whatever sensors a request asks for. A
  Service whose reader did not start (`MailboxSource::NoReader`) publishes no
  `firmware`, where managed captures it at construction whatever the device. Managed captures it once when the Service starts
  and never refreshes it, and so does this Service. A worker thread captures it
  after the first status publication, never on the read job, so it spends none
  of the job's HID budget. The record is published once, when the worker is done
  or 10 s have passed. A status before then has no `firmware` member, and a part
  still unread at 10 s is `Unavailable`. Every source is a Windows metadata
  read. The Service's laptop control handle is not used, and no command is sent
  to any device. A part that cannot be read is the managed text
  `Unavailable` and does not affect another part (the record has no null text).
  - BIOS, EC and model: the `HARDWARE\DESCRIPTION\System\BIOS` registry values
    `BIOSVersion`, `ECFirmwareMajorRelease` and `ECFirmwareMinorRelease`
    (`major.minor` with a two-digit minor, each an int that is a byte), and
    `SystemSKU`, mapped to the catalog's model number by its prefix.
  - VBIOS and GPU name: the one NVIDIA adapter under
    `SYSTEM\CurrentControlSet\Control\Video`, whose adapter and BIOS strings
    must both be text, without a leading "Version".
  - Thunderbolt: the `DEVPKEY` firmware version of the one USB4 root router
    with the exact instance, hardware ID, service and parent, when it parses as
    a .NET `Version`.
  - Touchpad: the BCD version number from the HID attributes of the one
    093A:3001 collection with usage 000D:0005. The RZ09-0581 touchpad is HID
    over I2C (`ACPI\PIXA3854`), so its interface path does not name its
    vendor. The interface is found by the devnode hardware ID that hidclass
    builds for every top-level collection, `HID\VID_093A&UP:000D_U:0005`, from
    the discovery scan's path and hardware IDs, and only interfaces with that ID
    are opened. They are opened with no access and shared for reading and
    writing, as managed opens them, and only `HidD_GetAttributes`,
    `HidD_GetPreparsedData` and `HidP_GetCaps` are called. No report is read or
    written. The vendor, product, usage and BCD version checks stay on the
    attributes. Managed opens every HID interface and then filters; native does
    not, so the laptop's own control collections are never opened. Selection is
    proven against this laptop.s registry; the open, the product ID and the BCD
    version have not yet been compared with the managed Service on the device.
  - Thunderbolt and the touchpad are read only on the exact RZ09-0581, which
    also carries a `ReadOnly` access for each. Any other model has neither
    read and a null access.
  - **McuVersion** is the one part not read by the capture worker. Managed
    reads it with `IBladeDevice.ReadMcuVersionAsync` inside each full verified
    observation while the record's version is `Unavailable` and fewer than
    three reads were made, and publishes it with that observation. Native
    sends the same read-only 00/87 query as the last query of the serialized
    read job (`service-live-readings-v1`), after the thermal read, the device
    mode and the fan speeds, through the protocol crate's
    `mcu_version::McuVersionQuery` and the job's transaction matching. Its
    admission is the read job's exact RZ09-0581 `1532:02E0` identity, and its
    command is pinned by the captured exchange in
    `evidence/legacy-captures/decoded/rz09-0581-pid-02e0-bios-3.01-mcu-version-fixture.json`
    (1.5.0.0). The query has its own 1 s budget and keeps the job's reserve, and
    its request goes out only while one exchange still fits, checked again
    just before it is sent. A timeout, another transaction, a malformed reply
    or a failed status costs no other reading and leaves `Unavailable`. Only a
    version from a reading the source accepted is kept: one read across a
    device change or Stop is discarded. Once read it is never read again or
    cleared. Before the record is published the version waits for it; a later
    version republishes the record, as managed's changed record does. A
    Service without a firmware source sends no query. The budget is three sent queries since the last
    resume, which the low-power gate's generation identifies. As in managed,
    only a resume restores it: a lock, an unlock or a power notice advances
    the read admission's lifecycle epoch but leaves the budget spent. A query
    that gets no terminal reply leaves its transaction ID quarantined for the
    process, so native also caps one read source at 12 sent queries, four
    budgets, whatever the resumes; managed has no cap. Native counts an
    attempt when its request is sent, so a dropped observation still spends
    one where managed returns the attempt of a canceled read. It has no
    device-loss reset: a lost lease ends the native read source for the
    process, so no later query is sent. RZ09-0528 has only summary evidence
    and no native reader, and the source-reviewed models have no managed MCU
    read, so their version stays `Unavailable`. The read has not been
    compared with the managed Service on the device.
- **Temperatures.** The CPU temperature stays null; the CPU temperature
  worker below is unchanged. The GPU temperature follows managed live
  readings, `SafeWindowsFanTemperatureSource.ReadForLiveReadingsAsync` since
  8331bbf4: the same user-mode `nvml.dll` behind the same two Windows reads,
  neither of which opens a GPU device or calls an NVIDIA API.
  - The workload: the NVIDIA adapter's WDDM
    `\GPU Engine(*)\Utilization Percentage` counters, opened fresh, primed and
    read again 150 ms later (`WindowsGpuActivitySource`). A process other than
    System (PID 4) above 0.1 % is a workload; unavailable counters are none.
    The counter query is the platform crate's
    `gpu_apps::NvidiaActivityQuery`, over the GPU-app sampler's reviewed PDH
    and DXGI reads.
  - The D-state: SetupAPI `SPDRP_DEVICE_POWER_DATA` of the one present
    display-class `PCI\VEN_10DE&` device (`WindowsNvidiaGpuPowerState`). None,
    several, a failed step or a property that is not a complete
    `CM_POWER_DATA` is unknown.

  NVML is loaded, started and read only for a D0 adapter, and a D-state
  other than D0 read right after the read discards the value. A sample with
  a workload reads NVML. A sample without one, idle or with unavailable
  counters, reuses the last valid D0 reading of any sample for up to 10 s
  while every sample since has reported D0. Otherwise it reads NVML once the
  samples have reported D0 for 3 s, so a GPU an idle app holds in D0 is read
  at most once every 10 s while its reads return a valid temperature and are
  not cancelled, and NVIDIA's own brief wakes open no NVML. Any sample that
  is not D0, the recheck after a read included, drops the reading and
  restarts the dwell. A sample cancelled right after its first D-state read
  changes neither, and a read cancelled by Stop keeps no value, as in
  managed.
  The 3 s dwell and 10 s spacing are managed's and have not been checked
  against NVIDIA's runtime-D3 idle timeout on the device. The session
  (`nvmlInit_v2` to `nvmlShutdown`) stays open across consecutive valid
  samples of an active D0 GPU and at most 5 s after the last one; an idle
  sample, even one that read NVML, a powered-down, unknown or failed sample,
  a sample cancelled after its D-state read and Stop end it before the
  sample returns. A sample that starts within 250 ms of the
  lease's end ends the session first, and the D-state read ends one whose
  lease ran out during the Windows reads, before NVML is called. Managed's
  timer ends a session on time even during a sample; native keeps it past
  5 s only while a PDH or SetupAPI read stalls. A missing driver, adapter or
  library, a failed call and a value outside 10..=125 C are null, as in
  managed. A value above the codec's 115 C bound is null where managed would
  refuse the reply. No failure status is kept: a failed NVML, PDH or
  SetupAPI call publishes null, and no diagnostics producer records why.
  `nvml.dll` is loaded with `LOAD_LIBRARY_SEARCH_SYSTEM32`, so neither the
  search path nor the current directory is searched for it or its
  dependencies, and it stays loaded for the process. Like managed's
  `[DllImport]`, the load checks no signature. A signer check is queued with
  the package import-allowlist review of `pdh.dll` and `dxgi.dll` below.

  The sample runs on its own worker thread, started by the first request,
  never on the read job. It starts beside the job, and the observation waits
  for it after the job's reading and before the admission check, at most
  250 ms and never later than 500 ms before its deadline, so a slow sample
  holds neither the reply nor the read source. A sample whose reading is not
  accepted or that has not arrived by then is dropped, and a reading that
  waited for its sample is never published over one from a later job. A
  sample whose observation stops waiting, or whose request's deadline,
  500 ms before the observation's, passes, before its NVML read stops after
  the step it is in. One that has started its NVML read by then still
  rechecks the D-state and keeps a valid reading for the next idle samples,
  as managed keeps one its token did not cancel; only Stop cancels it there.
  A sample that ends past its request's deadline publishes nothing. One
  request may wait behind a running sample and any other gets none; a
  waiting request whose observation went is skipped. Stop cancels without
  waiting: a running sample makes no call after the one it is in, a waiting
  request is skipped, and the worker ends the session when the call in
  progress returns, then exits. Managed reads the GPU only after a
  successful device read; native samples before the job ends, so a job that
  then fails may still have read an active D0 GPU and renewed its session for
  up to 5 s. It never starts NVML for a powered-down or unknown GPU, or for
  an idle D0 GPU before the dwell. `gpu-temperature-vectors.json` pins the
  activity decision, the power classification, the sampler's gate, idle
  reuse and dwell, session use and lease step by step, and the codec bound
  against managed
  (`ServiceControlRuntimeTests.NativeGpuTemperatureVectors`). The native
  sampler keeps managed's `ReadAsync` reader, which leaves an idle GPU
  unread, only to replay the rows that interleave both readers; the Service
  samples through the live reader alone. The image
  imports `pdh.dll` and `dxgi.dll`, which the package import allowlist does
  not admit; the live-readings image stays refused by its marker. The read
  has not been compared with the managed Service on the device.

- **GPU clock offsets.** `gpu_clock_offsets.rs`, in thermal-mutation
  compositions only, is the backend. Only `service-gpu-clock-tuning-v1`
  composes its production writer (`thermal_gpu.rs`), and only for the
  explicit performance requests (see
  [native thermal mutation](windows-native-thermal-mutation.md#gpu-clock-tuning-service-gpu-clock-tuning-v1)).
  Without that feature, and on every reconciliation path with it (profile
  saves included), `Storage::gpu_incomplete` still refuses a GPU effect. `setGpuClockTuning`
  is not served yet. The controller ports managed `NvidiaGpuClockOffsetController`:
  exactly one NVIDIA GPU, its exact RZ09-0581 name, the P0 ranges read with
  the versioned structure, core then memory written and read back, and each
  channel restored independently after a failure, with the fresh reading
  deciding between not applied and indeterminate. The management machine
  ports managed's six states, its policy clip to the RZ09-0581 envelope, its
  ungated first query of an uncertain submission, relinquish, resume
  invalidation and power-change rearm. A driver reading outside the envelope
  resolves to managed with no confirmation. As in managed, an uncertain
  submission is held in memory only: after a restart the state is Unknown and
  the next write's query reads the driver, so there is no durable journal by
  design. Stop, shutdown and low power write nothing, and the offsets stay in
  the driver, as in managed. The calls run on their own worker thread with a
  one-call queue. A call whose budget ends before it starts, or whose caller
  stops waiting, never starts; one that ends after it started, or that
  panics, is indeterminate. A panic leaves the worker serving, with its open
  NVML session shut down and an interrupted submission uncertain. The worker
  serializes single calls only: the integration must hold one exclusive
  thermal lane from a paired write's preflight through its completion, as
  managed's hardware-operation gate does, so a resume or relinquish cannot
  land between them. The submission itself rechecks the envelope and the
  reset rule, and reusing a confirmed reset is refused once a resume has
  withdrawn it. `nvml.dll` is loaded through the same System32-only loader as
  the temperature reader (`nvml_library.rs`) and, like it, checks no
  signature: a Windows trust and catalog-membership check in that loader is a
  tracked gate before any GPU clock-offset writer is packaged. Because a
  bare-name load returns an `nvml.dll` already loaded from anywhere, that
  check must verify the loaded module's path. All seven functions are
  resolved at load, where managed binds each on first use: a missing library
  or function reports NVML status 12 where managed reports the .NET load
  exception, and a driver without the setter refuses a write as Unavailable
  where managed fails the set as not applied. `gpu-clock-offset-vectors.json`
  pins the controller's calls, messages and driver state against managed
  (`NativeGpuClockOffsetVectorTests`) for a loaded library; the management
  machine is pinned by unit tests of the managed rules, not by recorded
  vectors. Nothing has run against a real driver.

Live readings appear only in the ObserveState reply, never in the subscription
feed, as managed does not push a change of live readings alone. A request for no
sensors publishes `liveReadings: null`. Reads outside an ObserveState, such as a
trigger's, ask for none.

Native publishes no capability set: managed's exact-model set would claim
controls this Service does not admit, and `capabilities: null` claims nothing.
The managed codec refuses a confirmed active HyperBoost without a HyperBoost
access, so a status whose confirmed state has an active HyperBoost, and only
that status, carries an observation record: managed
`DeviceCapabilities.Unavailable()` with `hyperBoostAccess: 0` (observed only)
and the exact RZ09-0581 `supportsTemporaryFullCharge: true`. That member is
not nullable, and the tray reads false as "this model has no temporary full
charge", which the model's battery evidence refutes; true only keeps the
tray's charge-limit actions waiting for the temporary state. The record admits
no control. The record follows the published confirmed state in every
projection: a fresh read, a retained read, an unavailable read and a
mutation's own read. It applies in every mailbox composition. A confirmed
state names no model, so the record is bound to its one producer, the exact
RZ09-0581 performance Reader: `capability_tests` pins that Reader's identity
to the record's and fails when another source file publishes an observation.

Still unavailable natively: a reply
for Custom High/High with Automatic fans, which the thermal read does not
confirm, so that ObserveState has no fresh read and no live readings. A failed
thermal read refuses the whole ObserveState, where managed still publishes the
battery power flow. The vectors in
`tools/openblade-windows-host/tests/data/live-reading-vectors.json` pin the
codec and the battery mappings against managed
(`ServiceControlRuntimeTests.NativeLiveReadingVectors`), and the managed codec
decodes the native frames in `live-reading-native-frames.json`
(`NativeLiveReadingFrames_DecodeThroughTheManagedCodec`).

The vectors in `tools/openblade-windows-host/tests/data/firmware-version-vectors.json`
pin the firmware record against managed
(`ServiceControlRuntimeTests.NativeFirmwareVersionVectors`): each row feeds one set
of Windows reads through `WindowsFirmwareVersionSource` and the two device
selectors, writes the result through the v90 status codec and is replayed
through the native capture. The same file pins `Version.TryParse` and
`Convert.ToInt32`, the two framework parsers the native code mirrors. The managed
codec decodes the native frames in `firmware-version-native-frames.json`
(`NativeFirmwareVersionFrames_DecodeThroughTheManagedCodec`). The vectors in
`tools/openblade-windows-host/tests/data/mcu-version-vectors.json` pin the 00/87
request and reply codec through managed `McuVersionProtocol` and replay the
managed three-read budget, with resume, through the native read source
(`ServiceControlRuntimeTests.NativeFirmwareVersionMcuVectors`).

#### Diagnostic export (read-only)

A Service built with `service-diagnostics-export-v1` serves `exportDiagnostics`
instead of replying that the operation is unsupported. The feature implies
`service-mailbox-observation-v1` and adds the capability marker
`service-diagnostics-export-v1`, last. Both package readers refuse the feature
and the marker by name ("The diagnostic export is not admitted for packaging."),
so no packaged image carries it.

The archive is managed `DiagnosticArchiveProvider` and
`DiagnosticBundleExporter` over evidence the Service already holds: the status a
GET would answer the caller with, the diagnostic history and the host metadata.
No firmware command is sent, no HID handle is opened and no storage is read
on the export path. The status is a fresh observation, though: the export runs
the Service's read worker once, which reads the power source, enumerates HID
devices through SetupAPI and PnP for the device metadata, and scans processes
for competing controllers. Entries keep
managed's names, order and text: `versions.json`, `device.json`,
`device-verification.json`, `capabilities.json`, `configuration.json`,
`command-results.json`, `service-history.json`, `firmware-queries.json`,
`hyperboost-ownership.json` and `logs.jsonl`, UTF-8 without a BOM, indented
with CRLF and escaped as `JavaScriptEncoder.Default` does. Redaction is
managed's: the sensitive-token plan (longest first, ordinal and case-insensitive)
over the same entries and members, and `DiagnosticText.Redact` with the
Service's own profile, local data, machine and user names on every history
value. The tokens are managed `ReadSensitiveTokens`, in its order: the
authorized users' SIDs, accounts and profile paths, the Service user and
machine, the profile and the program, common and local data folders with their
`OpenBlade` subfolders, and the image directory. An archive over 16 MiB fails
with managed's message, and the response frame has managed's 24 MiB bound.

| Entry | Native source |
| --- | --- |
| `versions.json` | The status `serviceVersion`, the RtlGetVersion text managed `Environment.OSVersion` prints, and the status `firmware` record (live-reading builds); `Unavailable` otherwise. No fresh firmware read runs. |
| `device.json`, `capabilities.json` | The status `firmware.modelNumber` against the contract's model catalog, and the status capabilities, as managed's status-only runtime. |
| `configuration.json` | The status members managed prints. The `capability.rz09-0581.*` keys, the three `hyperBoostBase` modes and the fan speed ranges are `Unavailable`: no native status carries them. |
| `command-results.json`, `service-history.json` | The bounded history below. |
| `device-verification.json` | Managed's `NotObserved` record: native has no device verification cache. |
| `firmware-queries.json` | `[]`: native keeps no firmware query cache. |
| `hyperboost-ownership.json` | Managed's `Missing` record: native has no HyperBoost ownership store. |
| `experimental-support.json` | Absent, as managed omits it without an experimental model. |
| `logs.jsonl` | Empty: native has no `ILogger` or trace producer. |

A status capability member that no native summary prints
(`customPerformance`, `cpuPowerLimit`, `intelCpuPowerLimit`, `localDimming`,
`cpuVoltageOptimizer`, `gpuOverclock`, `hyperBoostBaseModes`, a non-empty
`fanSpeedRangesByPerformancePreset` or
`observedPowerSourceBehaviors`) fails the export instead of omitting it. No
native composition publishes one today.

The history is managed `DiagnosticHistory`: 2048 entries, 4 Mi characters and
4096 characters a line, each value redacted, put on one line and truncated
with `...[truncated]`, oldest first evicted and an oversized entry dropped. Like
managed it lives only in process memory, so a restart starts empty and there is
no file or ACL. The profile store is its one producer: each stored
`updateProfiles` save records managed's `Service`/`Reconcile` `Stored`
summary. Managed's reconciliation summaries have no native producer yet, and a
composition without the profile store exports an empty history. A history
poisoned by a panicking producer is unreadable and fails the export with
"Diagnostic history is unavailable."

Admission is managed's default control admission: SYSTEM and the console
session's interactive caller export, and an inactive or remote interactive
caller gets "Another Windows session currently controls OpenBlade hardware."
A non-interactive caller closes the connection, the endpoint-wide native
convention, where managed replies "OpenBlade accepts control requests only
from an interactive Windows session." Native activates no user, so managed's
activation failures and its session-changed `Blocked` result have no native
step. The archive carries the caller's private status projection, so a caller
that no longer controls the session when the archive is ready gets no reply.
Any other failure is managed's "Diagnostic export failed: {message}".

One export builds at a time; a second waits. Managed serializes the export
with every finite request through its user gate; native reads only published
status, so only exports wait. The archive and its response frame, including
the base64 text, are built on the blocking pool with a 20 s bound, which
managed lacks, so a stalled build cannot hold the export gate. A requester
that disconnects, the bound and Stop (which aborts each connection task)
cancel the build at its next check. The build checks at each entry, between
the host token sources, and after at most 64 KiB of compression input plus one
match (258 bytes).

A build cannot be interrupted between checks, so one that passes its bound can
run on after its export has failed. The server remembers it: an export that
arrives while it still runs gets the same time-bound failure at once and starts
no second build, the rule of the status observation worker, so unfinished
builds never accumulate. The next export builds again once it ends. The gate
is server-owned state, not a process global.

Native writes its own deflate, so its compressed bytes and the exact content
size at which the 16 MiB bound is reached differ from managed's; entry text is
identical. Managed truncation counts UTF-16 units and can cut a surrogate
pair; native keeps whole characters. Native's user-path redaction matches
letters by Unicode alphabetic class where managed uses `\p{L}`.
Another deviation: managed's export refreshes nothing; native's status is the fresh observation
above, so an export can also fail with the status source's own error.

The managed vectors in
`tools/openblade-windows-host/tests/data/diagnostic-export-vectors.json` and
the contract in `diagnostic-export-contract.json` pin the redaction, history
retention and truncation, entry text for typed bundles and the refusal replies
(`NativeDiagnosticExportVectorTests`). The native tests replay them, and the
managed reader decodes the native responses recorded in
`diagnostic-export-native.json` through the v90 codec and compares each entry
with the archive the managed provider builds from the same status and history
(`NativeDiagnosticArchive_ReadsThroughTheManagedCodecAndMatchesTheManagedProvider`).
Set `OPENBLADE_REGENERATE_NATIVE_DIAGNOSTIC_ARCHIVES` to rerecord them.

#### Performance shortcut

A Service built with `service-performance-shortcut-v1` serves
`cyclePerformanceShortcut` and `setPerformanceShortcutPreview` instead of
replying that the operations are unsupported. The feature implies
`service-fan-mutation-v2` and adds the capability marker
`service-performance-shortcut-v1`, last. Both package readers refuse the
feature and the marker by name ("The performance shortcut is not admitted for
packaging."), so no packaged image carries it. A cycle writes only the
reviewed preset and Custom recipes through the ordinary performance request
path; a preview publishes status and reaches no firmware. See
[Performance shortcut](windows-native-thermal-mutation.md#performance-shortcut-service-performance-shortcut-v1)
for the selection, replies and differences from managed.

### Phase 10n ownership binding

The installed candidate's conditional firmware factory now requires a private
`Authority` port. It first obtains an opaque loaded-generation epoch, checks
its launch binding, and acquires one independent `OwnerLease`. The lease
must attest `Acquired` with that same epoch and the existing live firmware
admission. Only then can the factory construct the genuine Phase 10m worker.
`Lost`, `Releasing`, and exact-epoch `Retired` have distinct meanings.
Missing or mismatched evidence refuses the factory before hardware opening.
The installed composition supplies no authority while its independent source
is unavailable, so installed startup remains fail-closed.

The epoch token has no installed constructor. A production authority source
must retain the original process and pre-launch image binding, independently
confirm predecessor retirement, serialize ownership across replacement and
rollback, and preserve unresolved native and recovery fences. These facts
cannot be reconstructed from a PID, executable path, disk receipt, metadata
open, controller absence, local mutex, or request flag. The existing numeric
firmware epoch continues to bind session, controller, power and awake evidence.
A changed epoch or evidence loss seals this binding, even if earlier values
return. The exact RZ09-0581 selection and same-handle verification remain in
the reader before every transfer.

Shutdown revokes admission before awaiting worker retirement. The owner lease
can release only after terminal I/O, callback drain, handle closure and worker
join succeed. A pending shutdown retains the original independent lease. A native error,
failed release, or mismatched retirement quarantines it until process exit. Dropping the source retains
that fence for the process lifetime. Replacement and rollback require the
same independent authority to confirm retirement before successor acquisition.
No new source instance or process-local worker slot establishes that proof.

The managed reference paths explain the separate gates. The Service's
`WindowsVerifiedBladeDeviceProvider` owns a serialized hardware gate, while
`Rz090581FeatureCommandGate` coordinates its control and matrix sessions
within the managed process. `RazerCommandSessionGate` releases only after
successful native retirement. Provider disposal can return after its shutdown
budget while retaining a pending command and handle. Managed Setup stops the
service and checks guardian quiescence before payload activation. Its rollback
collects failures while restoring earlier lifecycle state. Those boundaries
require independent owner retirement before a successor can obtain authority.
The Rust role payloads use the same managed Setup transaction runner. The
conditional scratch receiver separately rehearses activation, fixture readiness
and commit, with payload rollback on failure.
The retained candidate verification contract binds the original process handle,
creation time, path and pre-launch build pin. Its concrete launch source and
the stronger writer-retirement gate currently belong to offline/scratch fixtures.
Native disk commitment and installed loaded-generation evidence remain separate
outstanding production gates.

Explicit in-memory fixtures exercise acquisition, loss during the real getter
reader, stale and mismatched epochs, refusal before the worker factory,
replacement/rollback refusal during unresolved retirement, and native drain
before owner release. Real Windows adapters are compiled and linked only.
One process-wide pool issues the transaction IDs 1 to 254 in pairs. The
firmware keeps its reply in a device mailbox that GET_FEATURE reads, and
neither canceling Windows I/O nor reopening the handle drains it. A request
whose SET did not take effect reads the older reply, which only the ID, class
and command tell apart, so the pool issues an ID again only with proof that no
reply bearing it can be read again:

- An ID that was never sent returns when its Reader retires.
- An ID whose correlated terminal reply was read (checksum, ID, class and
  command match; success, or a completed FAILED or NOT_SUPPORTED rejection)
  returns once a later request with the same class and command is answered.
  The firmware answers each request once, and the newer reply replaced it,
  whether the firmware keeps one reply buffer or one per command.
- An ID sent without such a reply (canceled, timed out, busy, malformed or
  uncorrelated) never returns in that process. A reopened handle proves
  nothing.
- IDs a fan record or a temporary-charge cancellation names stay pinned for
  the process: a later Reader sends them, and the journal requires a live fan
  owner's IDs to stay distinct from later battery IDs. A fan record pins them
  only once a setter may reach the device, so a fan request refused before
  its first setter, or one that prepares no record such as an Automatic
  no-op, returns them.
- A request is recorded as sent before it is submitted, and a pool whose lock
  is poisoned records nothing, so nothing is submitted.

Issue is round-robin, so a returned ID comes back only after every other free
ID. The process pool starts its round at a random ID; a fixed start would
issue first the IDs a previous process also issued first, whose replies may
still be in the mailbox. A random start makes such a collision unlikely but
cannot rule it out. So before the first job, the Service also quarantines up
to 80 IDs its thermal journal names whose replies an earlier process may have
left in the mailbox, answered or not. These are every ID of each family's
newest request and the live fan and temporary-charge owners' reservations,
then known-not-submitted requests and superseded sets (see
[thermal history](windows-rust-thermal-history.md)). IDs that only reads or
performance setters sent are not journaled, so for them the random start is
the only protection. Managed restarts its finite IDs at 1 for every reopened
session after a poisoned one; native does not, because the mailbox outlives
the handle and the process.

A read whose exchange keeps failing still spends one ID per job. A thermal,
battery or temporary-charge request then refuses with its transaction-capacity
reason, even when the baseline read is the first reservation to fail; read jobs
publish no reading. Pinned records no longer exhaust the pool. A fan record's
pins return once a durable row closes it: its own Automatic proof (50 or 51),
its known-unsubmitted Prepared row (6), or a newer owner's 30 or 50 row. A
temporary-charge enable's cancellation IDs return once the enable has left
every owner slot, any source-cancel claim on it is decided and its cancel has
joined. The settled pins wait in a per-Runtime queue until the next fan or
battery mutation Reader retires and unpins them, so reads and thermal requests
do not return them. A sent ID without a correlated reply stays quarantined, and
an unresolved or quarantined record keeps its pins. Measured on the in-memory
port, one process used to serve only about 12 manual fan set-point changes
(each pins 20 to 24 IDs), 14 alternations between manual and Automatic, or 30
temporary-charge enable and cancellation cycles (8 pinned cancellation IDs
each). The `pool_pins` readiness tests run longer sequences against a pool
already reduced below those budgets.
`Attempt::transfer` is the only path that marks a request sent or answered.
`transactions.rs` rebuilds the sent request to correlate its reply, which is
why the encoder guard lists it.

The replay in `tools/openblade-windows-host/tests/fixtures/get-status-v79.frames`
contains two consecutive managed codec frames recorded before the IPC80 bump.
The managed `IpcMessageCodec.WriteRequestAsync` wrote `GetStatusRequest(79, id)`,
then `WriteServerFrameAsync` wrote a successful `ServiceResponse` with the same
ID and the v79 replay status. The ID is `11111111-2222-4333-8444-555555555555`.
`observe-state-v79.frames` uses the same codec and request ID with confirmed
Performance on AC power. `subscribe-status-v79.frames` holds
`SubscribeStatusRequest(79, id)`, the correlated response and a v79
`ServiceStatusEvent` with the status revision incremented by one.

The Service status client and server now speak v90, so these three recordings
are rejection controls. `original_v79_wire_fixtures_are_rejected_before_dispatch`
replays each request in memory and checks that the installed dispatcher closes
it without a reply or a firmware read, then accepts the same request with its
version set to 80.
The installed server tests feed the managed replay into the same dispatcher
the candidate uses, including an update while the initial response is blocked.
The feed holds one latest revision and supplies both the active and inactive
views. The server rechecks the connected peer and resolves console-session
authority before selecting a view for the initial response and every event.
There are eight persistent status slots within a fourteen-connection budget.
Disconnect, inbound bytes, write timeout, publisher loss, lease rotation after
15 minutes, and shutdown release the stream and its slot.

After installed runtime admission, the startup `GetStatus` probe seeds the
subscription feed from the native read-only observer. Ordinary reads and
subscribers share that observer. The first subscription refreshes an observation
older than two seconds, and one service-lifetime poll checks for changes every
two seconds while subscriptions are open. Both caller views advance together
only when projected evidence changes. An expired observation replaces prior
evidence with unavailable status, and a fatal observer failure closes the feed.
The source reports observational metadata with writes blocked. Profile activation
and device ownership require separate admission. The offline exchange scripts
observer inputs and peer evidence over in-memory streams.

### Managed operations the installed Service does not serve

A well-formed v90 request that names a managed operation outside the
composition's typed decoder gets managed's own fallback reply: the correlated
`ServiceResponse` with `success: false`, error
`IPC request operation is unsupported.` and no body or error code
(`ControlRequestHandler.HandleUserRequestAsync`). The operation list is the
`operationFamilies` inventory in the
[managed replay contract](windows-backend-replay.md), which
`WindowsReplay_FrozenWireInventoryMatchesManagedRegistry` pins to the managed
`ServiceRequest` registry. Its 53 operations include `reportSessionPowerTransition`
and `updateProfiles`. An operation is served only when this build's features
dispatch it, so a read-only composition also refuses `setFanOverride` and the
other mutations instead of closing.

The refusal decodes only the object envelope: one `operation`, an Int32
`version` of 90 and a non-nil `requestId`, with duplicate members and serde
private member names refused. It never interprets the body, reads the status
source, opens the device or touches storage, so every caller the status
endpoint admits receives it. Unknown operation names, malformed frames, other
protocol versions and served operations that fail their own decoder still close
without a reply.

Managed reaches its fallback only after the session access policy and user
activation. Its codec rejects an invalid known-operation body before dispatch:
`NamedPipeControlServer` then replies `IPC request is malformed.` under a
fresh random `requestId` and closes. Native replies the unsupported refusal to
every admitted caller and does not validate the body. An inactive session
therefore gets the refusal where managed would send an access failure, and a
malformed body for an unserved operation gets the refusal, correlated to its
own `requestId`, where managed would send the uncorrelated malformed-request
failure. Neither path changes state.
`installed_unserved_operations_reply_with_the_managed_unsupported_failure`
contributes its actual frames to the `OPENBLADE_NATIVE_THERMAL_RESPONSES`
corpus, and the managed fact requires the unsupported reply to decode through
the v90 codec.

## Compile the installed service launch boundary

The standalone Windows host workspace has a separate `openblade-windows-service-candidate`
binary with common installed-service-candidate runtime and an explicit
service-metadata-only-v1 composition. It compiles separately
from both fixtures and accepts no process arguments. The SCM callback must supply only the service name
`OpenBlade`. Startup checks the exact quoted image command for
`<Program Files>\OpenBlade\OpenBlade.Service.exe`, automatic own-process
registration, the LocalSystem token, session zero, and the same nonzero PID from
SCM in `START_PENDING`.

```powershell
cargo +1.99.1 check --offline --locked --manifest-path tools/openblade-windows-host/Cargo.toml --features service-metadata-only-v1 --bin openblade-windows-service-candidate
```

The metadata entry authenticates the original signed package, exact role image,
compiled identity, current/frontier and submission before publishing a launch.
Its v2 artifact grants Service private custody and metadata-only recovery;
the combined Agent retains the same actual Cargo features with capabilities
none. V1 bytes retain their schema and exact commitments. V1 cleanup can
retain its original public-read/no-unprivileged-write custody descriptor only
with complete matching drains; it gains no abandonment privilege.

Metadata production excludes the real firmware and HID acquisition constructors
at compilation. The separate service-hardware-compile-check composition covers
those constructors without granting metadata-only recovery. Firmware observation
remains unavailable; installed status reports truthful hardware fields.

Recovery authenticates the old artifact and holds epoch writer, original
exclusive custody and the same independent owner/writer.lock in that order,
through proof and retirement publication. Only authenticated v2 metadata artifacts
can classify valid unmatched launches as abandoned metadata. Paired drains stay
distinct. Malformed evidence refuses retirement, and so does unresolved owner
history, except a crashed thermal producer whose exit the reacquired custody
proves; that generation is handed to managed, as
[thermal history](windows-rust-thermal-history.md#native-to-managed-handback)
describes. Immutable proof snapshots (v2, v3 with thermal history, v4 with a
handback) bind the complete attempt-set dispositions and independently
validated owner chain. Public readers may still block legacy
custody. Recovery preserves descriptors, launches, drains, counters, snapshots
and partial staging evidence. The separate endpoint seam
requires the installed ACL, caller admission, and a successful correlated v90
`GetStatus` response with a status snapshot on `OpenBlade.Service.v2`, bound to
the admitted PID. The candidate publishes `SERVICE_RUNNING` only after that
evidence. Setup's external probe still runs afterward. The fixture endpoints
and their scripted status have separate roles.

### Rehearse startup before payload commit

`SetupCommandRunner.RunAsync` activates the payload, starts the Service and
checks its health before calling `SetupPayloadTransaction.CommitAsync`. The
scratch Setup receiver rehearses that order with a private readiness exchange.
Activation verifies the live Service and Session Agent files against the
authenticated package claim and publishes `candidate-package.startup.json`
while those files and the active deployment binding are retained.

The explicit fixture build pins the Rust receipt verifier by SHA-256. Setup
opens that image in the protected scratch tree, retains it across direct launch
and commit, and verifies the returned process handle, creation time, and image
path. The verifier derives the issuer PID from the connected local pipe peer,
then retains the process and reads its creation time and fixed scratch image
path. It checks that generation, the receiver image, the active binding and the
launch secret against the original broker launch evidence for the deployment
request. Setup passes the secret through the verifier's inherited stdin. The
observation channel exposes only the commitment to the test harness.

The verifier checks the provisional receipt and both role images before
replying on the protected first-instance pipe. Setup waits for the child to
reserve that first instance before connecting. The two exchanges bind the same
deployment request and startup commitment to distinct challenge IDs and steps.
Each side checks the peer on that connection. Setup retains the verifier
generation through readback and rechecks it just before publishing the committed
record. A standalone reader can inspect committed bytes only. The issuer proof
is scoped to the standard-token fixture and its trusted launcher and binaries
in the same-user scratch tree.

The verifier releases its record and role handles after accepting readback.
Setup independently rechecks both roles and the claim, retires the exact startup
record, and publishes `candidate-package.committed.json` by retained-handle
rename. A pre-publication failure rolls back the activated tree and restores the
prior receipt when present. A dead receiver or changed generation invalidates
the startup exchange. The committed reader still requires its separate phase
and commitment.

A Windows install rehearsal needs evidence that Burn authenticated the package
and the live Setup launch, and that SCM started the intended signed service
generation. Capture a trusted binding from SCM's process object to the actual
loaded image and its authenticated role identity, plus the private pipe peer
and same generation through Setup readback and publication. Verify production
endpoint admission only after commit, and observe registered lifecycle and
prior-install restoration when pre-commit readiness fails. Reboot and
interrupted publication need an authenticated recovery rehearsal. In the
scratch rehearsal the verifier is the launched process and the Service and
Session Agent role images are checked as files.

Managed Setup has a separate process observation seam for an explicit fixture
handoff. It duplicates the original Windows launch handle once. Each read uses
that same process object to confirm its PID, creation FILETIME, exact image path
and that it has not exited. Closing the caller's handle does not affect the
retained duplicate. A live observation is a snapshot and does not attest to the
loaded image bytes.

The offline Setup fixture supplies the expected Service-role length and digest
from a separate source before staging. Setup retains the local NTFS ancestor
handles without delete sharing and the exact image without write or delete
sharing. It checks the held objects' identities, paths, reparse status and ACLs,
and hashes the image through the same bounded read handle. The trusted fixture
launcher receives the pinned path and returns the original launch handle. The
combined `ISetupRetainedCandidateLaunch` rechecks both pins at the readiness
boundaries and rejects loss of either.

The fixture test launches a scratch copy of the Windows command interpreter.
Its build expectation is fixture authority only. Installed activation still
requires authenticated Service-role evidence from the package and a trusted
handoff for the actual SCM launch.

A separately authorized disposable VM must confirm the actual SCM callback
arguments, LocalSystem session, image command and PID availability in
`START_PENDING`. If SCM omits the PID, the candidate rejects the launch until
there is another independent binding. Once the installed providers are
integrated, verify the typed probe, stop and endpoint retirement, restart, and
rollback in that VM before any payload admission.

## Run the isolated test

Use the pinned Rust toolchain, Windows MSVC tools and the repository's .NET SDK. From the repository root in PowerShell:

```powershell
Push-Location rust
try {
    $manifest = '../tools/openblade-windows-host/Cargo.toml'
    cargo test --locked --manifest-path $manifest
    cargo build --locked --manifest-path $manifest
} finally {
    Pop-Location
}
$env:OPENBLADE_RUST_DEV_HOST_EXE = (Resolve-Path 'tools/openblade-windows-host/target/debug/openblade-windows-host.exe').Path
dotnet restore src/OpenBlade.Tray/OpenBlade.Tray.csproj
dotnet test tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj --filter FullyQualifiedName~RustReadOnlyHostTests
Remove-Item Env:OPENBLADE_RUST_DEV_HOST_EXE
```

The integration tests launch their own foreground processes and assign fresh pipe IDs. To run the host interactively, invoke the executable with `foreground <id>` or `foreground-background <id>` and a 32-character lowercase hexadecimal ID. Use a fresh ID generated with `[Guid]::NewGuid().ToString('N')`. It prints `READY` after successfully claiming `OpenBlade.Rust.ReadOnly.Dev.<id>`. Send a newline or close its input to stop. The latter mode exercises inactive-session projection for an admitted same-logon client.


## Drive the runtime replay

Run `foreground-replay <id>` with a fresh development pipe ID. After `READY`, send one lowercase `n` to standard input for each step. With an interactive console, press Enter after each `n`. Close standard input to stop. The test harness controls the replay clock through these ordered steps. Pipe reads and subscriptions do not advance it.

| Revision | Input | v90 status |
| --- | --- | --- |
| 1 | Initial synthetic startup | Plugged-in profile, unknown function-key readback, writes blocked |
| 2 | Recovery getter returns no value | Readback stays unknown and the in-memory intent stays pending |
| 3 | Battery power observed | Desired performance comes from the battery profile. Confirmed performance stays unknown |
| 4 | Recovery getter reports Multimedia Keys | The observed mode becomes visible. The requested Function Keys intent stays as a tombstone |

The recovery inputs follow the managed restart cases in `service-runtime.json`. The existing Rust recovery reducer plans the getter and computes the settlement. The controller applies that settlement to its one-entry in-memory journal. Before the battery observation, the script cancels a recovery read and delivers its stale result. Neither event changes the snapshot or journal. The performance policy selects the battery preset from the fixture. The writes block stays set after settlement, as the replay omits the full observation that could lift it.

Each v90 subscription starts at the latest snapshot and can receive newer revisions. The stream keeps only the latest pending snapshot per receiver, so slow subscribers may skip revisions. Reconnect starts at the latest revision without repeating earlier effects. v51 uses its existing passive projection.

This phase exercises the startup recovery and power-profile paths with scripted observations, a fake journal and no transport setter. Full startup and run-marker orchestration, protected storage and its recovery gates, native observation and command execution, session ownership, and the remaining profile and feature paths still need parity work.

## Drive the durable configuration replay

The same Windows interoperability suite runs the durable scenario against the foreground host. The first process creates a fresh fixture directory with a host-generated ID below the Windows Local AppData known folder's Temp directory. It prints `ROOT <fixture-id> <file-identity>` for the harness, seeds the original three managed documents from the [configuration fixture](windows-configuration-recovery.md), and inspects them from disk before preparing status or claiming the pipe. The C# harness holds the fixture directory open without delete sharing until both processes finish. The fixture root and endpoint IDs are independent.

| Invocation | Behavior |
| --- | --- |
| `foreground-durable <pipe-id>` | After `READY`, one lowercase `n` commits the second managed aggregate. v90 publishes the new requested charge limit only after the writer verifies the commit and the startup inspector independently reads the committed bytes. |
| `foreground-durable-restart <pipe-id> <fixture-id> <file-identity>` | Reopens the pinned namespace read-only, verifies the root identity and inspects the exact stored documents before opening the pipe. |
| `foreground-durable-interrupt <pipe-id>` | One lowercase `n` injects an error after the profile replacement and before restoration. The process exits with the sidecar and remaining artifacts intact. A fresh restart refuses before readiness or pipe creation. |
| `foreground-profile-bootstrap <pipe-id>` | Builds the typed 80 percent battery policy and default Fan Stop settings over the managed fixture, commits to an empty root, and reads back the current aggregate before publishing status. |
| `foreground-profile-migrate <pipe-id>` | Seeds a synthetic schema 24 aggregate, decodes the 77 percent charge policy and saved Fan Stop settings, and commits the current schema 27 against the exact inspected preimage. The managed oracle checks that each cutoff rises by 10 °C up to 100 °C and that the other settings survive. |

The original replay reports an 80 percent requested charge limit before the step and 75 percent after the commit and restart. The profile modes report 80 percent for bootstrap or 77 percent for migration, including on restart. All modes use the existing synthetic recovery reducer with an unavailable getter result. Confirmed battery state stays unknown, writes stay blocked, and v51 keeps its passive fixture projection. The standard input command advances this fixture only. The pipe still refuses mutations.

The original replay compares the managed documents byte for byte. The profile modes compare JSON values with the managed bootstrap and migration oracle, verify marker digests over the emitted bytes, and check that restart preserves those exact bytes. The Rust serializer uses its own formatting and member order. The interruption test keeps the original marker visible, verifies backup and stage contents, and compares every retained artifact before and after refusal. It also rejects a changed root identity, a path argument and a directory junction. Successful runs end on standard input EOF. The managed harness removes its fixture after the processes exit. If driving the modes directly, use a fresh pipe ID each time and remove the reported fixture directory after inspecting the result.

The executable can write only a newly created fixture root. It pins plain directory components below the Windows known folder on a fixed drive and creates the fixture relative to the pinned Temp handle. It uses fixed filenames and embedded fixture data. A single worker runs the transaction while the pipe can still serve the cached snapshot. Shutdown joins an accepted transaction and surfaces any failure. The read-only restart gate accepts the two original complete aggregates and the two current aggregates generated by the explicit bootstrap and migration modes. It verifies the marker hashes before decoding the battery policy and Fan Stop settings. A damaged, older, or unexpected snapshot causes startup refusal and stays on disk. The private pipe reports the requested limit, and the managed oracle and process tests compare the migrated Fan Stop document. The remaining profile and preference fields must match the managed fixture. Schema 1–23 and profile-only migration, model-specific validation, and persistent overrides remain outside this slice. This developer boundary relies on the test account controlling its temporary files. Production still needs protected path and ACL admission, coherent snapshots and cross-process ownership, specialty journal integration, operator recovery, and native owner takeover gates. The interruption is injected and provides no evidence about sudden power loss.

## Compose battery policy with a migrated profile

Run `foreground-battery-profile <id>` on a fresh private pipe. The host migrates
the same managed schema 24 fixture before readiness. It loads that verified
profile into the scripted RZ09-0581 controller. The 77% saved limit, the
controller's 80% getter result, and the temporary override are separate.
The existing v90 `updateProfiles` request accepts only a charge-limit edit
against the otherwise unchanged fixture. As in the managed service, it stores
the profile without a setter and clears a temporary limit only if equivalent.
The existing battery operations require the battery scenario's fresh admission
and exact readback. A lost acknowledgement keeps the prior confirmed and
requested values visible with a pending write. A scripted getter can settle
the request without another setter.

With `foreground-battery-profile-interrupt <id>`, the first accepted profile
edit interrupts the same transaction after profile replacement and before
rollback. If the prior configuration cannot be verified, the process retains
the last verified saved policy, blocks further writes, and preserves the
sidecar and staged data. Independent battery observations still settle any
pending hardware result. A new
`foreground-battery-profile-restart <id> <root-id> <file-identity>` process
opens only a verified current snapshot and refuses unresolved artifacts before
claiming the pipe. On successful restart, the script supplies a fresh
independent device baseline. Temporary intent has process lifetime.
These modes own no hardware, and the restart mode has read-only configuration
access. Other profile edits, production recovery, and ownership transfer remain
outside the scenario.


## Exercise temporary full charge

`foreground-full-charge <pipe-id>` starts the unified foreground host with a
private migrated fixture and no profile writer. The existing v90
`setFullChargeOverride` request exercises the exact RZ09-0581 recipe through
encoded scripted Power Mode Control reports. Enable and cancel confirm the
override and unchanged charge cap and protection independently of the setter
reply. The fake also exercises an unavailable flag, firmware completion, a
battery-power enable refusal, an unrelated Power Mode Control bit, and a
lost SET response. Reconciliation requires both readings and performs no
setter. A completed one-shot is retired in memory. The managed client test
checks that every saved configuration file keeps its original bytes.

This mode uses a private pipe and an inert device. Production integration
still requires Windows owner, lifecycle and fresh exact-device write admission
and physical verification of the native host.

## Exercise standard battery policy

Run `foreground-battery-parity <id>` on a fresh private development pipe. The
same-logon v90 client can send `setBatteryPolicy` and `setBatteryProtection` to this
mode. All other mutations are refused. v51 keeps its passive status projection. The
foreground, runtime replay, and SCM modes keep their existing refusal behavior.

The fake device represents the RZ09-0581 (1532:02E0) [reviewed battery command
table](../src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_BatteryProduction.json).
It starts at 80% with protection on and a confirmed inactive temporary-full-charge
flag. The capture-backed thresholds are 50% through 80% in five-point steps.
Protection off uses its dedicated typed setter. Turning protection on from off sets
the configured limit in one operation. A scoped limit change requires protection to be
on and the current and requested limits to be admitted. An unknown
temporary-full-charge flag blocks new policy writes on this query-capable path.

The scenario loads the baseline profiles from test-owned storage. Direct requests
update temporary intent after independent confirmation and leave the saved profile
intact. After a lost acknowledgement, the confirmed snapshot and committed intent stay
separate from the pending request. A fresh read-only observation may commit a matching
pending intent. An unavailable observation leaves it pending, and a mismatch publishes
the observed state without replaying the request. If the source changes while a request is unconfirmed, the next successful
observation selects the new profile without committing the old request or issuing
a setter.
On reconstruction from the same
saved profile, scoped overrides and pending intent are absent. Startup in this harness
observes state without applying a profile.

After `READY`, stdin accepts one lowercase `n` for each fixed scenario step. Use the
v90 status stream to observe the step before sending the next request.

| Step | Effect |
| --- | --- |
| 1 | Arm cancellation before the next eligible fake setter. The canceled request closes without a result or a write. |
| 2 | Arm a lost acknowledgement after the next eligible fake setter takes effect. |
| 3 | Run one read-only observation to reconcile any pending intent. |
| 4 | After recovery, select the battery profile and reconcile protection. The global limit override remains. |
| 5 | Select the USB-C profile through the same battery-only transition. |

The managed runtime test and Rust fake-device tests cover the same standard operation
sequence. The tray test checks the typed results and current status through the real
C# client. The scenario has no HID transport or production storage. Temporary full
charge, RZ09-0528 and source-reviewed model paths, real power and device admission,
and the rest of the full-service transition remain separate work.

## Exercise a standard performance override

Run `foreground-performance-parity <id>` on a fresh private development pipe.
The same-logon v90 client can send `setPerformanceOverride` with Balanced or
Performance, a temporary or persistent lifetime, or an explicit null to clear.
The scenario fixes the RZ09-0581 (1532:02E0) on full adapter power with Automatic
fans. It starts at Balanced, with no HyperBoost or manual fan ownership. The
[exact-device thermal capture](../evidence/legacy-captures/decoded/rz09-0581-pid-02e0-bios-3.01-thermal-write-validation-fixture.json)
records the pair and its independent readback.

The controller reads both thermal channels through the scoped coordinator check
and the port's own preflight. If the first read already matches, the operation
returns Applied without a setter. Otherwise the fake device receives the
destination's two `0D/02` commands in channel order and must echo each payload
exactly. Independent `0D/82` reads must agree before the confirmed preset changes.
Performance uses `01010200` then `01020200`. Balanced uses `01010000` then
`01020000`. The selected path leaves the fans on Automatic throughout.

The operation stores a persistent override before attempting hardware admission
or a write. A temporary override clears the persistent slot and changes in-memory
intent. Clearing returns to the saved profile without editing it. A canceled
request can leave accepted intent in storage while the last published snapshot
stays unchanged until the next publication. The test store survives controller
reconstruction in the Rust tests. Each new foreground process starts with a
fresh store.

After `READY`, send one lowercase `n` for each fixed step. Wait for the status
event before sending the next request.

| Step | Effect |
| --- | --- |
| 1 | Arm cancellation before the first eligible setter. The request closes without a result and submits no command. |
| 2 | Arm a lost echo after the second setter changes the fake device, and fail the immediate reconciliation read. The result is Failed and performance writes stay blocked. |
| 3 | Observe the fake device read-only. Matching readback publishes the preset and clears the block without replaying either setter. |

The managed runtime oracle checks the typed operation, storage order and
observed outcomes separately from the Rust replay. The tray test uses the
existing `ServiceClient` and v90 status stream. Rust tests also check the
exact payloads and partial-pair failure. Native admission, durable production
configuration and restart recovery, power-source transitions, other presets,
Custom, HyperBoost, manual fan ownership and handback, Guardian, and other
thermal routes remain outside this scenario. The installed service remains
the only live owner.

## Exercise ordinary fan ownership

Run `foreground-fan-parity <id>` on a fresh private development pipe. The
same-logon v90 client admits a persistent Fixed 4,000 RPM fan override. The
scripted RZ09-0581 (1532:02E0) starts in Performance with Automatic fans and
full adapter power.

The typed recipe follows the [managed port trace](../tests/Fixtures/Parity/rz09-0581-ordinary-fan-transitions.json).
Entry changes both fan selectors to manual while retaining Performance. A
paired getter confirms that state before either RPM target is submitted.
Handback changes only the fan selector pair. Fresh adapter checks precede
each write in both transitions. A repeated Fixed request on confirmed manual
topology sends the two targets directly because the getter cannot report the
requested RPM.

The host stores the Fixed request in memory and publishes the firmware's
Automatic or manual topology separately. Confirmed manual topology alone
does not establish ownership. Handback requires an owned, confirmed manual
state and current synthetic admission. A split or unavailable read preserves
the last confirmed topology and gates further writes after possible submission.
Read-only reconciliation never grants ownership or replays the saved request.

After `READY`, send a lowercase `n` for each step and wait for its status event.

| Step | Effect |
| --- | --- |
| 1 | Invoke synthetic lifecycle handback. Preserve the saved Fixed request and Performance. |
| 2 | Arm a lost reply after the first selector changes. |
| 3 | Read the split pair without issuing a write. |
| 4 | The scripted device restores the Automatic pair externally. A read-only observation clears the block. |

Native lifecycle integration still needs current owner-epoch continuity, fresh
controller and prior-authority cleanup admission, and durable production
recovery. This mode uses only the private foreground pipe, scripted device and
process-local intent. Fan Stop, HyperBoost and other presets or models are
outside its scope.

## Compose durable profiles with thermal replay

`foreground-thermal-profile <pipe-id>` loads the migrated configuration used by
the battery/profile scenario. One private pipe serves battery, profile, standard
performance and ordinary Fixed fan operations. The scenario guard serializes
these effects and publishes one status revision. The thermal recipes use the
existing exact-device scripted transports. The bounded composition runs at
full-adapter power with an Automatic baseline. Fan entry and handback require
confirmed Performance. Fixed entry also requires a fresh Automatic fan pair.
A manual pair observed during reconciliation stays outside the entry recipe.

The C# client exercises preset cancellation, explicit preset selection, Fixed
entry and owned Automatic handback. A lost final fan response leaves the last
confirmed topology visible until a later getter reads the pair. That observation
does not acquire ownership. The durable battery profile edit remains separate
from temporary battery and thermal requests. The temporary Fixed intent stays in
memory through handback. `foreground-thermal-profile-restart <pipe-id> <fixture-id>
<file-identity>` loads the verified profile from disk with a fresh scripted device.
Production override persistence and device recovery are separate work.

`foreground-thermal-profile-interrupt <pipe-id>` interrupts the same profile
transaction while the fan write is unresolved. Quarantine blocks further mutations
while a read-only fan observation can still resolve the device state.

## Private CPU temperature worker

The development host crate tests the CPU worker supervisor over an in-memory
duplex stream and a Windows-only protocol fixture behind the
`cpu-worker-fixture` feature. Both use the managed worker's 8-byte request and
20-byte reply formats. Readiness consumes a sequence without producing a
sample. Sequence numbers continue across worker replacement. A reading rounds
up and ages from its own request.

A caller may stop waiting for initialization while the worker continues under
its own startup deadline. A timeout after a send attempt, cancellation during
an exchange, or a terminal reply retires the stream. Replacement requires
confirmed exit and any retry cooldown. Unavailable readings never reuse a
temperature.

The fixture launcher supplies the absolute image path and an explicit stdio
handle list to `CreateProcessW`. Windows assigns the child to the unnamed,
non-inheritable kill-on-close job as part of creation. The launcher releases the
private pipe stream to the supervisor after creation and pipe registration
succeed. The parent uses overlapped I/O and the child writes only two typed
replies. Standard error goes to `NUL`. The original process handle must signal
before replacement. The stop deadline bounds the caller's wait, while the
observer retains the handle and keeps checking after a timeout or a failed
wait. Diagnostics distinguish the native launch or I/O stage from the
exit-observation stage.
[Process creation attributes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute) ·
[Job objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects) ·
[Process handle waits](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-waitforsingleobject)

Run the CPU worker unit tests and the no-image test on Windows with the pinned
toolchain. Run the ignored smoke separately in the ordinary-user sandbox. It
copies the Cargo-built fixture to a fresh task-owned directory and launches it
once.

```powershell
$manifest = 'tools/openblade-windows-host/Cargo.toml'
cargo +1.99.1 test --offline --locked --manifest-path $manifest --features cpu-worker-fixture --lib cpu_worker
cargo +1.99.1 test --offline --locked --manifest-path $manifest --features cpu-worker-fixture --test cpu_worker_windows
cargo +1.99.1 test --offline --locked --manifest-path $manifest --features cpu-worker-fixture --test cpu_worker_windows -- --ignored --exact exchanges_with_an_inert_job_owned_fixture
```

Installed thermal admission still requires a protected and verified worker
image, the approved PawnIO and CPU sensor admission path, and fresh sample
evidence wired into the installed thermal policy. The fixture returns a
constant synthetic value and the development host has no CLI mode that launches it.

## Replay a power-source profile transition

Run `foreground-power-profile <pipe-id>` with a fresh ID. The private host loads
the same migrated profile documents as the thermal replay. Each stdin `n`
advances one scripted lifecycle or effect step. The C# ServiceClient reads and
subscribes on the private pipe. Only the script can apply effects in this mode.

Windows external power starts as ExternalUnknown. The existing scripted adapter
query confirms a source after two matching known adapter states within five
attempts. Conflicting known states stop classification, even when both supply
enough power. The last external profile can stay visible with writes blocked.
Battery and limited adapter notifications select their saved profiles for
display. Their preset command and thermal admission are outside this replay.

A limited-power notification supersedes a pending full-adapter effect. The old
callback remains stale even after another full-adapter notification. The current
effect applies the saved Balanced preset directly from confirmed Performance
with a freshly observed Automatic pair. Scripted adapter reads guard the two
typed channel setters. Independent readback confirms Balanced and the stored
profile documents remain byte-for-byte intact.

Run `foreground-power-profile-uncertain <pipe-id>` to lose the final setter
response and immediate readback. The previous confirmed preset stays visible.
A newer limited-power notification cannot consume its transition while the
write remains unresolved. A later getter settles the preset without replaying
a setter. A fresh notification can then select the limited-power profile.

Live Windows notification delivery, protected recovery, and exact-device
preflight and submission through the production lifecycle are separate gates.
The broader lifecycle also needs owned HyperBoost and fan handback, the
appropriate battery and limited-power preset recipes, and session admission.


## Replay keyboard Off

`foreground-lighting-off <pipe-id>` admits the existing v90 keyboard Off
override on a scripted RZ09-0581 (1532:02E0) with no owned renderer. The
saved profile selects Off, so clearing the override selects that same
profile setting. The fixture records persistent and temporary intent in
separate process-local slots. A temporary override clears the persistent
slot. The profile documents remain byte-for-byte intact.

The managed port and Rust replay use the scoped brightness getter, the
port's preflight getter, the zero-brightness setter, its verification
getter, and the final scoped getter. A scoped zero skips the setter.
The independent logo observation remains separate. Nonzero keyboard
brightness leaves the active effect unknown. No renderer is acquired.

After `READY`, send one lowercase `n` per step and wait for the status event.

| Step | Effect |
| --- | --- |
| 1 | An external controller restores nonzero brightness. New Off intent can be stored while writes are blocked. |
| 2 | The controller leaves. The next setter's port verification sees zero, then the final scoped getter loses its response. |
| 3 | A read-only getter still observes nonzero brightness and the write fence stays set. |
| 4 | A read-only getter observes zero after an external change and clears the fence. |

The getter fault expires at the next script step if unused.
A matching setter echo or intermediate read cannot settle an unavailable
final read. The replay keeps the accepted override while the final state is
unknown and never repeats the setter during reconciliation.

Use `foreground-lighting-off-owned <pipe-id>` to start with one
synthetic Spectrum frame in flight. Submit keyboard Off after the
"Renderer frame in flight" event. Admission closes while the renderer
still owns its simulated port. Send two `n` steps within 60 seconds
of closure. The first completes the frame and the second releases
the port so a connected caller can continue to the brightness checks.
An early `n` leaves the fixture waiting for Off. If the caller
disconnects, wait for the host's disconnect status before advancing.
Retirement and stored intent survive that disconnect. A fresh request
can apply Off after verified release. If the deadline expires, late
steps can drain the worker while the fence stays in place. The four
brightness steps above follow successful retirement.

`foreground-lighting-off-uncertain-owner <pipe-id>` follows the same
first two steps but returns an unverified port release. The third `n`
probes replacement ownership and supplies an external brightness zero.
The fixture retains its fence without confirming Off. The managed
ordinary Off path joins the frame on the same port. The managed
invalidation path separately retains the closing port until cleanup
completes, even when a caller cancels its wait.

Production native renderer transport and lifecycle, fresh hardware
admission, protected override persistence, restart and power/session
transitions, logo changes, visible effects, and other devices remain
separate work.

## Application context replay

Run the existing managed client against `foreground-application-context <id>`
with a fresh development pipe ID. This foreground mode accepts v90
`ReportApplicationContext` only after the same native pipe peer check used by
the other modes. A scripted authority stands in for the active local interactive
session. It is checked again where the report changes state.

The in-memory fixture has one enabled executable rule with different AC and
battery intent. A matching report leases it for 15 seconds. Only increasing
sequences from the current agent instance can renew it; a new agent instance
can start a fresh sequence. A null or unmatched executable clears the selection.
Expiry clears the selection while retaining the sequence watermark, and process
restart clears both. The executable is only evidence supplied by the Session
Agent. The development host does not inspect process lifetime or treat a path
as proof that an application is running.

Successive `n` steps change the scripted session to inactive and back to active, then
records a persistent performance override, selects battery power, disables the
application rule, and enables it again. The managed test checks typed results
and status updates, including precedence, expiry and recovery. This mode only
projects desired performance and gaming behavior for its fixture. It has no
configuration writer or device adapter, and leaves the durable profile format
and the other modes' effect admission unchanged.

## Replay application selection in the composed runtime

Use `foreground-integrated-application <id>` for a disposable private writer.
The companion mode `foreground-protected-integrated-application <id> <root>`
uses the existing protected-writer admission for a precreated test root.
Both modes use the same private v90 pipe, composed runtime, and renderer
shutdown sequence as the integrated fixture. The executable rule is a
fixture input in memory. Lease reports, expiry, and selected presets leave
the saved profile aggregate untouched.

An accepted report selects the current source's application override. A
temporary manual preset takes precedence until cleared or until power
changes. Reports and lease expiry publish desired intent. They leave
confirmed state to the scripted preset and independent readback path.
Battery and limited power select their desired profiles while the scoped
preset recipe stays closed. Each full-adapter effect still checks the
session, exact-device admission, fresh adapter, and Automatic fan pair.

After `READY`, report the fixture executable with the managed client.
The first `n` reconciles that preset. The next steps select battery,
revoke and restore session control, then classify and apply full-adapter
power using the currently selected profile. Let the lease expire on full
power before continuing. The next step loses the setter response and
immediate readback while returning to the saved full-power profile. The
following step is fenced. The final step performs a read-only observation
to settle the result. The renderer keeps its independent release signal
and drains on STOP even if an application lease is active.

## Replay private Fan Stop ownership and handback

Run `foreground-fan-stop <pipe-id> <case>` on the Windows development host.
Only the active v90 test endpoint accepts the existing typed
`setFanStopMode` request. The scenario represents the exact SO6120 EC 1.04
entry with a fixture clock, Balanced readback and resolved USB-C. It shares
the manual-entry fixture and drives Phase 5q's guardian replay using inert
reservation, owner, signal, exit and receipt observations.

In `complete`, send Stop and advance one stdin `n` to confirm it. Send
Automatic and advance three times: independent Auto readback, the owner's
cleanup receipt while exit is pending, then independent exit and durable
receipt verification. The ordinary fan fence stays closed through the first
two observations. After release a separate `n` supplies the full-adapter
context required by the existing scripted 4,000 RPM fan request. That
request is a canary for the ordinary write gate.

`incomplete` omits cleanup confirmation. `stale` repeats the last live owner
revision after Stop. `ambiguous` loses certainty about the Automatic signal.
Each retains recovery and refuses repeat writes. `refused` proves that Stop
was never submitted, then waits for exit before releasing as Inactive.
`entry-stale`, `entry-power`, and `entry-console` refuse before dispatch.
Stop and Automatic replies wait for the relevant proof; dropping the
Automatic caller leaves the scripted owner active. Each process scripts at
most one operation.

These cases exercise the existing managed client, private pipe framing and
typed status. They do not launch a guardian or CPU worker, inspect protected
journals, monitor native lifecycle events, acquire a hardware lease, send a
device command, or validate physical readback. The service, IPC schema and
product ABI are unchanged.

## Compatibility boundary

- Each connection accepts one request. Every mode serves v90 `getStatus` and `subscribeStatus` and v51 `getStatus`. Mutations use v90 in the explicitly scoped foreground modes. The v90 response and event use the [managed replay fixtures](windows-backend-replay.md). In the fixed replay modes, the subscription first sends the preceding synthetic revision and then one exact newer fixture event. v51 returns only the CLI fixture's approved fields and closes. Request IDs correlate replies. Every read uses in-memory fixtures and has no activation or state changes. It closes every other version, including the frozen v79, v80 and branch v85 requests, without a reply.
- Framing uses a signed 32-bit little-endian byte count and caps bodies at 64 KiB. There is a two-second first-frame deadline and a two-second deadline per write. Invalid lengths, malformed envelopes, invalid identity and unsupported versions close the connection. Each mode returns a correlated refusal for operations outside its allowlist. The host allows eight concurrent handlers and four concurrent subscriptions, with one additional listening instance and a private first-instance claim. The host promptly closes connections above capacity and retries a temporarily busy listener replacement for up to two seconds.
- Windows claims the first instance exclusively and rejects remote connections. The explicit DACL restricts access to the host account. After reading the request bytes, the host resolves the pipe token and requires the same SID, logon authentication ID and Windows session. This is a same-logon development trust boundary. The ordinary duplex C# client asks for generic write, which also gives its SID permission to create pipe instances. It does not establish server trust against another process in that logon. Both projections exercise synthetic authorization inputs behind the native identity check. They do not replicate active-session selection or LocalSystem access policy.
- Rust tests cover the wire fixtures and native endpoint checks, including first-instance contention, listener saturation and recovery, an anonymous client, and start/stop ordering through the shared lifecycle with an injected SCM reporter and stop signal. The .NET tests exercise the real tray client through the foreground endpoint, including initial status, event, cancellation and reconnect. They supplement that path with managed codec and v51 raw frames. The Windows job in the Rust workflow runs both sets on stacked PRs.

### Known compatibility gaps

- With null capabilities the tray's `ProfileAdmissionPolicy.Supports` refuses every profile draft. Beside an observed HyperBoost, the observation record lets it admit a draft that changes nothing, and it still refuses any change. Native profile admission stays authoritative, so the tray is unchanged.
- Session Display IPC stays at version 2 although main changed what it means. Manual setters now admit `ExternalUnknown` with fresh Service external-power evidence, and the display checkpoint admits source 3 as an unowned manual hold. The native Agent implements both. A version-2 peer built before that change refuses an `ExternalUnknown` manual request and treats a source-3 checkpoint as invalid, so automation pauses with every field uncertain. Both outcomes fail closed. The version number cannot tell the two behaviours apart.
- The read-only protocol-51 projection keeps a confirmed Custom preset whose levels the firmware did not report, with `customPerformance` null. Protocol 90 admits that state; every v51-era managed reader rejects it, because `ValidPerformanceState` requires `IsWellFormed`, so such a status fails closed. This branch made that choice before the reconciliation and did not revisit it.

### Pending parity with main a8aea95f

The merge of main a8aea95f moved the branch protocol 85 (a confirmed Custom
preset with unreported levels) to protocol 90, because main had assigned 85
through 89. Every managed vector recorder was rerun through the real managed
code. The only native replays that then differed were the eight `reader: live`
GPU sampler rows of 8331bbf476ab4f17a2c70d4945a77782049aa4a8, which gave
managed live readings `ReadForLiveReadingsAsync` and its idle D0 reads. They
were held in a `PENDING_PARITY` allowlist in `gpu_temperature_tests.rs` until
the native sampler ported that path (see **Temperatures** above). Every
sampler row now matches, so the allowlist and its companion test were
removed. A later replay that differs and cannot be ported at once goes into
such an allowlist again, with its case and the full managed commit.

Wire members the native Service does not publish at protocol 90. Each decodes
as its managed default, which is the honest state for a native build that has
no backend for it:

- `status.windowsDynamicLightingAdvisory` is absent and decodes as `Unknown`.
  The native Service has no Windows Dynamic Lighting advisory.
- `capabilities` stays null except beside a confirmed HyperBoost state. That
  observation record carries `hyperBoostEntryRestriction: null` and
  `blade14KeyboardEdition: null`, as managed writes them for
  `DeviceCapabilities.Unavailable()`. It omits main's
  `intelCpuPowerLimit`, `firmwareGamingGpuOverclockAccess` and
  `localDimming`, which decode as null.
- `confirmedState.localDimmingActive` is absent and decodes as null
  (unknown). The native Service has no local-dimming read.
- `coolingPad` is absent. The native Service has no cooling-pad backend, so it
  publishes neither the protocol 85 Auto mode nor its profile.
- `reportWindowsDynamicLightingAdvisory` and `clearLocalDimming` get managed's
  unsupported reply in every composition.

Managed changes from main a8aea95f with no native port. Their rerecorded
vectors are unchanged, so no native replay fails, but no vector exercises the
new behavior either. Each one stays a gap until it is ported and pinned by a
managed vector:

| Managed commits | Behavior | Native area |
|---|---|---|
| 85182893 | A pending write block is released on a power change | thermal mutation, drift. Native holds no published write block for a power change to release: admission never reads `writesBlocked`, and the pending-confirmation fence (`Storage::pending`, which makes `admit_trigger` return Fenced) deliberately outlasts a power change until read-only reconciliation, so porting this would mean releasing that fence |
| 3b692b95, 445f17b7, 2b141ccd | HyperBoost journal handoff retirement and exit submission | thermal history store, thermal mutation |
| 374e4847, 66c6e902, 72137077, 26db283c, cc2dea76 | GPU clock offset confirmation, resend and NVML submission recovery | the RZ09-0581 NVML backend ports the controller's submission recovery and the management states. With `service-gpu-clock-tuning-v1` the explicit performance requests reach it through the paired and GPU-only setters; `setGpuClockTuning`, the reconciliation paths and the status projection of confirmed offsets are pending, and that feature is refused by both package readers |
| 24966354, c91181b8 | GPU clock offsets and the NVIDIA laptop form on RZ09-0529 and RZ09-0530 | no native GPU clock offset writer; stored offsets are only validated |
| a26e1519 | A HyperBoost exit whose Maximum Fans restore was refused and whose journal cannot be retired reports the restore failure and blocked writes | HyperBoost exit reporting |
| 0cf60756 | Writes stay open after a refused Maximum Fans restore | HyperBoost exit, Custom capability |
| d379614f, 677608a4, 4570d54c, 5b2b549d | SnapTap driver keyboard admission, rearm and retry | Agent function row |
| 4afe8fe2 and the cooling-pad series | Cooling-pad Auto mode and profile | no native cooling-pad backend |
| 5dec9093, a1728389 | Windows Dynamic Lighting advisory | no native backend |
| 7c56a32c, 4cca8813, 8bb17a4d, 736f6d8a, b82a614d, 5e74b33f | Observed CPU power, Intel ranges, GPU overclocking and local dimming | no native backend |
| 520b7d03 and the lighting series | RZ09-0530 MCU edition key maps and MCU host lighting frames | no native backend |

Ported from main a8aea95f and pinned by a managed vector:

| Managed commits | Native behavior | Pinned by |
|---|---|---|
| 3f7b3838, 1a03e358 | Native latches no power-only block, so the destination already applied at the next classified power change. The origin tick now publishes the classified Windows source once per lifecycle epoch before it reconciles; see [Power change after conflicting adapter classes](windows-native-thermal-mutation.md#power-change-after-conflicting-adapter-classes) for the deviations | `power-only-release-vectors.json`, `readiness_tests/power_only_release.rs` |
| 05ec5381 | `Snapshot::verify` refuses a status whose capabilities carry a `oneShotLaptopControls` or `hostManagedLaptopControls` member that is present and not an array; an absent one stays accepted. The rest of the capability record stays opaque | `service-object-frames.json` rows run through both codecs, `service_status/object_tests.rs` |

Managed changes from main a8aea95f that do not apply to anything native
admits:

| Managed commits | Behavior | Why no native port |
|---|---|---|
| e4f19946, b3b2b5ae | The limiter stays off while the override was last on; an unreadable 07/8F blocks only override enable and cancel | Both change the experimental 0x02A0, 0x02C5 and 0x02C7 backends (`ExperimentalBladeModels`, `ExperimentalBladePort`) and their `ChargePolicyIndependentOfTemporaryFullCharge` capability, and admit temporary full charge on ExternalUnknown power. Native admits battery commands only on exact RZ09-0581 (`02E0`), where battery policy and temporary full charge need classified power. The shared read that reports protection off with bit 04 as an unknown override is already an unavailable native battery group (`BatteryObservation::projection`), with thermal reads independent of it. The `ResolveExplicitBatteryBaseline` fallback runs only on ExternalUnknown, which native refuses for battery |
| e481be77 | Raw 6 off battery is a transitional DC Balanced only after a transition from battery | The service rule needs a capability table with writable HyperBoost that lists Balanced but not Battery Balanced (`ReadsRawSixAsTransitionalDcBalanced`), which only the RZ09-0483 and RZ09-0482 experimental backends have; 02A0 never matches. Native admits thermal writes only on RZ09-0581, whose table lists Battery Balanced and whose HyperBoost is observed only. Native decodes 0581 raw 6 as Battery Balanced on any power |
| ea5bd41e | Raw 6 off battery reads as transitional DC Balanced | `ExperimentalBladePort` only, gated by `UsesRaw6AsBatteryBalanced` (029F and 029D, Synapse products 671 and 669) |
| 70cbb567 | Custom can be entered from raw 6 off battery | `ExperimentalBladePort.Custom` only, for 029F, 029D and 02B7 |
| 06577e09 | A firmware-only lost acknowledgement is judged from the preset | The fix stops the unresolved-NVML predicate from judging writes without GPU tuning. Native has no GPU clock writer, so a native lost acknowledgement is only ever judged from the preset readback |
| a63f884c, 262c543c, 561c43bb, 927c2e18, b872ba70, ac6b1215 | Deferred presets under manual fans and their reconciliation | Each targets backends whose capabilities set `ReportsPerformanceWithManualFans`, which only `ExperimentalBladePort` sets, or the preset-ranged fan restore, which needs the experimental `FanSpeedRangesByPerformancePreset`. Managed runs RZ09-0581 on the shared-selector shape, where Balanced under manual fans reads as Maximum with no preset, and the commits leave that shape unchanged. Native matches it: a preset chosen under manual fans is stored with the `ManualDeferred` reply, and a fan return to Automatic applies the saved destination |
| 1aee2bd6 | The fan exit on preset-reporting backends skips an undeclared Performance restore | Only backends that set `ReportsPerformanceWithManualFans` (experimental) are affected. Native fan recipes have no declared-component step runner |
| acdb3bf2, 185c145a | HyperBoost stops report cleared Maximum Fans, and an unrepresentable flag keeps the refusal | `ExperimentalBladePort.HyperBoost` only. Native submits no HyperBoost entry or exit |
| e22d97ac | Custom matches compare the confirmed Maximum Fans flag | It is gated by `SupportsMaximumFans` (029F, 029D and 02A0). Native never admits the flag on RZ09-0581: `Destination::custom` names no destination when `maximumFans` is true, and native publishes `maximumFans: false`, so a flag-only change cannot reach a native comparison |
| d4249eca | Custom Maximum Fans on RZ09-0482 | Experimental RZ09-0482 admission only |
| cc9b97ff, 363805c9 | Product 669 Power Mode Control acknowledgements | `ExperimentalBladePort` and `ExperimentalBladeModels` only (RZ09-0482). Native has no RZ09-0482 backend |
| db536537, b5a3aa6f | Temporary full charge on RZ09-0530 and RZ09-0529 | `ExperimentalBladeModels` admission only. Native temporary full charge is exact RZ09-0581 |
| 0426e6a4, a4bc0874 | HyperBoost entry by SKU with a Balanced handoff, and the RZ09-0510 Turbo base | Native submits no HyperBoost entry. The contract members they add are protocol 86 shapes that native already writes as managed defaults at protocol 90: `hyperBoostEntryRestriction` is null and `hyperBoostBaseModes` is null, so there is no Turbo slot. Native publishes a confirmed HyperBoost only with Automatic fans, which the widened check still accepts. Managed's RZ09-0581 production port is unchanged |

Diagnostic export. Main added these managed `DiagnosticArchiveProvider` keys.
Each reads only status members, so the native export prints them from the
status it publishes, and the managed archive comparison
(`NativeDiagnosticArchive_ReadsThroughTheManagedCodecAndMatchesTheManagedProvider`)
pins them. No export case is allowlisted:

| Managed commit | Keys | Native value |
|---|---|---|
| b82a614d7800ddd8367d2a57d51c90ea17a78c50 | `performance.intelCpuPowerLimit.{observed,write,rangesKnown}`, `display.localDimming.{observed,clear}` | False. `intelCpuPowerLimit` and `localDimming` join the capability records that refuse the export when present. |
| b82a614d7800ddd8367d2a57d51c90ea17a78c50 | `performance.firmwareGamingGpuOverclock.{observed,write}` | Read from the status access member, as `hyperBoostAccess` is; absent, so False. |
| 5dec9093266deb4169eba33b2e592b0029bb5e93 | `lighting.windowsDynamicLightingAdvisory` | The status member, absent, so managed's `Unknown`. |
| 0426e6a4efd66bb99ef350af409ade09ebf20861 | `capability.hyperBoostEntryRestriction` | The status capability member, null, so `None`. |
| a4bc0874790d6d7b18e9e72d95524e75158bdc1f | `capability.hyperBoostBase.Turbo` | `Unavailable`, as the other base modes. |

The export gaps listed under [Diagnostic export](#diagnostic-export-read-only)
are unchanged: no reconciliation summaries, logs, device verification,
firmware query or HyperBoost ownership sources.

## Native SCM gate

The executable links the native dispatcher and registers the stop/shutdown handler from its `--scm` entry point. That mode is reserved for an externally provisioned development service named `OpenBladeRustReadOnlyDev` and the fixed pipe `OpenBlade.Rust.ReadOnly.Dev.scm`. It uses the same account/logon admission, so a regular desktop client should be denied across the service logon boundary. The development executable does not provision a service.

The Rust workflow runs the native lifecycle smoke only on GitHub-hosted Windows VMs for same-repository pull requests. It checks a clean namespace, stages a hash-verified copy of the tested binary, and registers the demand-start service as LocalService. It verifies that a pre-existing pipe causes startup to fail, then observes RUNNING with an available listener and STOPPED with the pipe retired on two start/stop cycles. The runner's different-account pipe open must be denied. Cleanup stops and deletes the service and removes the staged binary.

The foreground interoperability suite still owns admitted-client and in-flight connection coverage. The hosted service smoke uses a separate account and does not test an admitted service-logon subscriber during SCM STOP or establish product multi-user policy. A test requiring those conditions needs a separately authorized disposable Windows VM.

## Exercise the private Session Battery report

Build `openblade-rust-session-agent` from `tools/openblade-windows-host/Cargo.toml` with the pinned toolchain. Set `OPENBLADE_RUST_SESSION_AGENT_EXE` to that binary and run `OpenBlade.Tray.Tests` with `--filter FullyQualifiedName~TrayBatteryClient`. These tests use the existing managed `SessionBatteryReportAdapter` with a fresh private pipe name.

Run `openblade-rust-session-agent battery-foreground <id> <scenario>` for a manual fixture. The ID is 32 lowercase hex digits. The scenario is `available`, `missing`, `malformed`, `oversized`, `failed`, or `stalled`. The host prints `READY` after reserving its first instance of `OpenBlade.Rust.Session.Battery.Dev.<id>`. Send a newline or close stdin to stop. The fixture reuses the private pipe owner and per-request peer admission. Its only inputs are the fixed scenario names. Append `battery-available` to the existing `integrated` command to serve the fixed available report alongside Display and GPU. All three listeners must claim the same fixture owner before readiness. Without this option, the integrated fixture leaves Battery off.

The reference is merge commit `d4ee4c367a2d0b01c7ed99a7b02eddba9515512a`. The managed source is `src/OpenBlade.Session/SessionBatteryIpcCodec.cs`, `SessionBatteryMessages.cs`, and `WindowsBatteryReportModels.cs`, plus `src/OpenBlade.SessionAgent/SessionBatteryPipeServer.cs`, `SessionBatteryRequestDispatcher.cs`, `WindowsBatteryReportProvider.cs`, and `WindowsBatteryReportParser.cs`. The synthetic timeline comes from `TryParse_ReadsStatusAndClipsTypedTimelineToTheAvailableDay` in `tests/OpenBlade.SessionAgent.Tests/WindowsBatteryReportParserTests.cs`. The same test project has `WindowsBatteryReportIpcTests.cs` and `WindowsBatteryReportProviderTests.cs` for wire and acquisition behavior.

Rust projects the Windows XML report fields through the inbox text reader. It enforces the managed 4 MiB XML, 1,024 usage entry, 512 interval, 24 hour window and 256 KiB IPC caps. The timestamp parser handles the report's ISO dates and timestamps at 100 ns precision, with explicit offsets normalized to UTC and missing offsets treated as UTC. The managed invariant date parser accepts additional free-form date strings. The inbox XML reader also has its own structural quotas. Both parsers preserve report order for equal interval starts.

The Rust acquisition seam has no live caller. Inert tests cover the private attempt directory, 15-second deadline, bounded stop confirmation and output, and nonrecursive cleanup. Before considering live wiring, separately verify actual Windows report encoding and timestamp forms, current-user process launch, process-tree ownership, and cancellation cleanup. The managed provider remains the default.
