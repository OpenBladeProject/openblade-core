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

## Compile the installed service launch boundary

The standalone Windows host workspace has a separate `openblade-windows-service-candidate`
binary behind the `installed-service-candidate` feature. It compiles separately
from both fixtures and accepts no process arguments. The SCM callback must supply only the service name
`OpenBlade`. Startup checks the exact quoted image command for
`<Program Files>\OpenBlade\OpenBlade.Service.exe`, automatic own-process
registration, the LocalSystem token, session zero, and the same nonzero PID from
SCM in `START_PENDING`.

```powershell
cargo +1.99.1 check --offline --locked --manifest-path tools/openblade-windows-host/Cargo.toml --features installed-service-candidate --bin openblade-windows-service-candidate
```

The installed admission seam currently returns failure. Integration needs
independent signed image and protected path evidence for the running candidate,
plus the installed controller and ownership checks. The separate endpoint seam
requires the installed ACL, caller admission, and a successful correlated v79
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

The Rust probe requires the startup record, a commitment from the live scratch
Setup receiver, and the matching active binding and Setup process generation.
It verifies both role images through retained handles and exits before Setup
receives the readiness acknowledgment. Commit retires the startup record and
publishes `candidate-package.committed.json` as its last fallible step. Refused
readiness and pre-publication failures roll back the activated tree, restoring
the prior tree and receipt when present. An interrupted receiver can leave
provisional bytes on disk. A dead receiver or changed generation invalidates
the startup exchange, and the committed reader requires its own exact phase
and commitment.

Installed integration still needs authenticated Burn-to-Setup authority, a
private readiness route that works before commit, independent evidence of the
generation SCM actually loaded, and post-commit authorization for the
production control endpoint. Reboot admission needs its own authenticated
commitment and recovery policy. The rehearsal verifies scratch files and
processes only.

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

| Revision | Input | v79 status |
| --- | --- | --- |
| 1 | Initial synthetic startup | Plugged-in profile, unknown function-key readback, writes blocked |
| 2 | Recovery getter returns no value | Readback stays unknown and the in-memory intent stays pending |
| 3 | Battery power observed | Desired performance comes from the battery profile. Confirmed performance stays unknown |
| 4 | Recovery getter reports Multimedia Keys | The observed mode becomes visible. The requested Function Keys intent stays as a tombstone |

The recovery inputs follow the managed restart cases in `service-runtime.json`. The existing Rust recovery reducer plans the getter and computes the settlement. The controller applies that settlement to its one-entry in-memory journal. Before the battery observation, the script cancels a recovery read and delivers its stale result. Neither event changes the snapshot or journal. The performance policy selects the battery preset from the fixture. The writes block stays set after settlement, as the replay omits the full observation that could lift it.

Each v79 subscription starts at the latest snapshot and can receive newer revisions. The stream keeps only the latest pending snapshot per receiver, so slow subscribers may skip revisions. Reconnect starts at the latest revision without repeating earlier effects. v51 uses its existing passive projection.

This phase exercises the startup recovery and power-profile paths with scripted observations, a fake journal and no transport setter. Full startup and run-marker orchestration, protected storage and its recovery gates, native observation and command execution, session ownership, and the remaining profile and feature paths still need parity work.

## Drive the durable configuration replay

The same Windows interoperability suite runs the durable scenario against the foreground host. The first process creates a fresh fixture directory with a host-generated ID below the Windows Local AppData known folder's Temp directory. It prints `ROOT <fixture-id> <file-identity>` for the harness, seeds the original three managed documents from the [configuration fixture](windows-configuration-recovery.md), and inspects them from disk before preparing status or claiming the pipe. The C# harness holds the fixture directory open without delete sharing until both processes finish. The fixture root and endpoint IDs are independent.

| Invocation | Behavior |
| --- | --- |
| `foreground-durable <pipe-id>` | After `READY`, one lowercase `n` commits the second managed aggregate. v79 publishes the new requested charge limit only after the writer verifies the commit and the startup inspector independently reads the committed bytes. |
| `foreground-durable-restart <pipe-id> <fixture-id> <file-identity>` | Reopens the pinned namespace read-only, verifies the root identity and inspects the exact stored documents before opening the pipe. |
| `foreground-durable-interrupt <pipe-id>` | One lowercase `n` injects an error after the profile replacement and before restoration. The process exits with the sidecar and remaining artifacts intact. A fresh restart refuses before readiness or pipe creation. |
| `foreground-profile-bootstrap <pipe-id>` | Builds the typed 80 percent battery policy and default Fan Stop settings over the managed fixture, commits to an empty root, and reads back the current aggregate before publishing status. |
| `foreground-profile-migrate <pipe-id>` | Seeds a synthetic schema 24 aggregate, decodes the 77 percent charge policy and saved Fan Stop settings, and commits schema 25 against the exact inspected preimage. The managed oracle checks that each cutoff rises by 10 °C up to 100 °C and that the other settings survive. |

The original replay reports an 80 percent requested charge limit before the step and 75 percent after the commit and restart. The profile modes report 80 percent for bootstrap or 77 percent for migration, including on restart. All modes use the existing synthetic recovery reducer with an unavailable getter result. Confirmed battery state stays unknown, writes stay blocked, and v51 keeps its passive fixture projection. The standard input command advances this fixture only. The pipe still refuses mutations.

The original replay compares the managed documents byte for byte. The profile modes compare JSON values with the managed bootstrap and migration oracle, verify marker digests over the emitted bytes, and check that restart preserves those exact bytes. The Rust serializer uses its own formatting and member order. The interruption test keeps the original marker visible, verifies backup and stage contents, and compares every retained artifact before and after refusal. It also rejects a changed root identity, a path argument and a directory junction. Successful runs end on standard input EOF. The managed harness removes its fixture after the processes exit. If driving the modes directly, use a fresh pipe ID each time and remove the reported fixture directory after inspecting the result.

The executable can write only a newly created fixture root. It pins plain directory components below the Windows known folder on a fixed drive and creates the fixture relative to the pinned Temp handle. It uses fixed filenames and embedded fixture data. A single worker runs the transaction while the pipe can still serve the cached snapshot. Shutdown joins an accepted transaction and surfaces any failure. The read-only restart gate accepts the two original complete aggregates and the two current aggregates generated by the explicit bootstrap and migration modes. It verifies the marker hashes before decoding the battery policy and Fan Stop settings. A damaged, older, or unexpected snapshot causes startup refusal and stays on disk. The private pipe reports the requested limit, and the managed oracle and process tests compare the migrated Fan Stop document. The remaining profile and preference fields must match the managed fixture. Schema 1–23 and profile-only migration, model-specific validation, and persistent overrides remain outside this slice. This developer boundary relies on the test account controlling its temporary files. Production still needs protected path and ACL admission, coherent snapshots and cross-process ownership, specialty journal integration, operator recovery, and native owner takeover gates. The interruption is injected and provides no evidence about sudden power loss.

## Compose battery policy with a migrated profile

Run `foreground-battery-profile <id>` on a fresh private pipe. The host migrates
the same managed schema 24 fixture before readiness. It loads that verified
profile into the scripted RZ09-0581 controller. The 77% saved limit, the
controller's 80% getter result, and the temporary override are separate.
The existing v79 `updateProfiles` request accepts only a charge-limit edit
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
private migrated fixture and no profile writer. The existing v79
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
same-logon v79 client can send `setBatteryPolicy` and `setBatteryProtection` to this
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
v79 status stream to observe the step before sending the next request.

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
The same-logon v79 client can send `setPerformanceOverride` with Balanced or
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
existing `ServiceClient` and v79 status stream. Rust tests also check the
exact payloads and partial-pair failure. Native admission, durable production
configuration and restart recovery, power-source transitions, other presets,
Custom, HyperBoost, manual fan ownership and handback, Guardian, and other
thermal routes remain outside this scenario. The installed service remains
the only live owner.

## Exercise ordinary fan ownership

Run `foreground-fan-parity <id>` on a fresh private development pipe. The
same-logon v79 client admits a persistent Fixed 4,000 RPM fan override. The
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

`foreground-lighting-off <pipe-id>` admits the existing v79 keyboard Off
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
with a fresh development pipe ID. This foreground mode accepts v79
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
Both modes use the same private v79 pipe, composed runtime, and renderer
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
Only the active v79 test endpoint accepts the existing typed
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

- Each connection accepts one request. Every mode serves v79 `getStatus` and `subscribeStatus` and v51 `getStatus`. Mutations use v79 in the explicitly scoped foreground modes. The v79 response and event use the [managed replay fixtures](windows-backend-replay.md). In the fixed replay modes, the subscription first sends the preceding synthetic revision and then one exact newer fixture event. v51 returns only the CLI fixture's approved fields and closes. Request IDs correlate replies. Every read uses in-memory fixtures and has no activation or state changes.
- Framing uses a signed 32-bit little-endian byte count and caps bodies at 64 KiB. There is a two-second first-frame deadline and a two-second deadline per write. Invalid lengths, malformed envelopes, invalid identity and unsupported versions close the connection. Each mode returns a correlated refusal for operations outside its allowlist. The host allows eight concurrent handlers and four concurrent subscriptions, with one additional listening instance and a private first-instance claim. The host promptly closes connections above capacity and retries a temporarily busy listener replacement for up to two seconds.
- Windows claims the first instance exclusively and rejects remote connections. The explicit DACL restricts access to the host account. After reading the request bytes, the host resolves the pipe token and requires the same SID, logon authentication ID and Windows session. This is a same-logon development trust boundary. The ordinary duplex C# client asks for generic write, which also gives its SID permission to create pipe instances. It does not establish server trust against another process in that logon. Both projections exercise synthetic authorization inputs behind the native identity check. They do not replicate active-session selection or LocalSystem access policy.
- Rust tests cover the wire fixtures and native endpoint checks, including first-instance contention, listener saturation and recovery, an anonymous client, and start/stop ordering through the shared lifecycle with an injected SCM reporter and stop signal. The .NET tests exercise the real tray client through the foreground endpoint, including initial status, event, cancellation and reconnect. They supplement that path with managed codec and v51 raw frames. The Windows job in the Rust workflow runs both sets on stacked PRs.

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
