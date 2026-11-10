# Windows Rust development service

The development host serves synthetic status over a local Windows named pipe. The existing WinUI `ServiceClient` can connect by name through its existing constructor. The tool is a standalone Cargo workspace outside the product's Rust dependency graph and publish inputs. The installed C# service remains the hardware owner.

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

## Compatibility boundary

- Each connection accepts one request. Every mode serves v79 `getStatus` and `subscribeStatus` and v51 `getStatus`. Mutations use v79 in the explicitly scoped foreground modes. The v79 response and event use the [managed replay fixtures](windows-backend-replay.md). In the fixed replay modes, the subscription first sends the preceding synthetic revision and then one exact newer fixture event. v51 returns only the CLI fixture's approved fields and closes. Request IDs correlate replies. Every read uses in-memory fixtures and has no activation or state changes.
- Framing uses a signed 32-bit little-endian byte count and caps bodies at 64 KiB. There is a two-second first-frame deadline and a two-second deadline per write. Invalid lengths, malformed envelopes, invalid identity and unsupported versions close the connection. Each mode returns a correlated refusal for operations outside its allowlist. The host allows eight concurrent handlers and four concurrent subscriptions, with one additional listening instance and a private first-instance claim. The host promptly closes connections above capacity and retries a temporarily busy listener replacement for up to two seconds.
- Windows claims the first instance exclusively and rejects remote connections. The explicit DACL restricts access to the host account. After reading the request bytes, the host resolves the pipe token and requires the same SID, logon authentication ID and Windows session. This is a same-logon development trust boundary. The ordinary duplex C# client asks for generic write, which also gives its SID permission to create pipe instances. It does not establish server trust against another process in that logon. Both projections exercise synthetic authorization inputs behind the native identity check. They do not replicate active-session selection or LocalSystem access policy.
- Rust tests cover the wire fixtures and native endpoint checks, including first-instance contention, listener saturation and recovery, an anonymous client, and start/stop ordering through the shared lifecycle with an injected SCM reporter and stop signal. The .NET tests exercise the real tray client through the foreground endpoint, including initial status, event, cancellation and reconnect. They supplement that path with managed codec and v51 raw frames. The Windows job in the Rust workflow runs both sets on stacked PRs.

## Native SCM gate

The executable links the native dispatcher and registers the stop/shutdown handler from its `--scm` entry point. That mode is reserved for an externally provisioned development service named `OpenBladeRustReadOnlyDev` and the fixed pipe `OpenBlade.Rust.ReadOnly.Dev.scm`. It uses the same account/logon admission, so a regular desktop client should be denied across the service logon boundary. The development executable does not provision a service.

The Rust workflow runs the native lifecycle smoke only on GitHub-hosted Windows VMs for same-repository pull requests. It checks a clean namespace, stages a hash-verified copy of the tested binary, and registers the demand-start service as LocalService. It verifies that a pre-existing pipe causes startup to fail, then observes RUNNING with an available listener and STOPPED with the pipe retired on two start/stop cycles. The runner's different-account pipe open must be denied. Cleanup stops and deletes the service and removes the staged binary.

The foreground interoperability suite still owns admitted-client and in-flight connection coverage. The hosted service smoke uses a separate account and does not test an admitted service-logon subscriber during SCM STOP or establish product multi-user policy. A test requiring those conditions needs a separately authorized disposable Windows VM.
