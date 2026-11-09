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

## Compatibility boundary

- Each connection accepts one v79 `getStatus` or `subscribeStatus` request, or one v51 `getStatus` request. The v79 response and event use the [managed replay fixtures](windows-backend-replay.md). In the fixed replay modes, the subscription first sends the preceding synthetic revision and then one exact newer fixture event. v51 returns only the CLI fixture's approved fields and closes. Request IDs correlate replies. Every read uses in-memory fixtures and has no activation or state changes.
- Framing uses a signed 32-bit little-endian byte count and caps bodies at 64 KiB. There is a two-second first-frame deadline and a two-second deadline per write. Invalid lengths, malformed envelopes, invalid identity and unsupported versions close the connection. Outside the two opt-in parity modes, mutations receive a correlated refusal before any dispatch. Each parity mode accepts only its own v79 mutation operations. The host allows eight concurrent handlers and four concurrent subscriptions, with one additional listening instance and a private first-instance claim. The host promptly closes connections above capacity and retries a temporarily busy listener replacement for up to two seconds.
- Windows claims the first instance exclusively and rejects remote connections. The explicit DACL restricts access to the host account. After reading the request bytes, the host resolves the pipe token and requires the same SID, logon authentication ID and Windows session. This is a same-logon development trust boundary. The ordinary duplex C# client asks for generic write, which also gives its SID permission to create pipe instances. It does not establish server trust against another process in that logon. Both projections exercise synthetic authorization inputs behind the native identity check. They do not replicate active-session selection or LocalSystem access policy.
- Rust tests cover the wire fixtures and native endpoint checks, including first-instance contention, listener saturation and recovery, an anonymous client, and start/stop ordering through the shared lifecycle with an injected SCM reporter and stop signal. The .NET tests exercise the real tray client through the foreground endpoint, including initial status, event, cancellation and reconnect. They supplement that path with managed codec and v51 raw frames. The Windows job in the Rust workflow runs both sets on stacked PRs.

## Native SCM gate

The executable links the native dispatcher and registers the stop/shutdown handler from its `--scm` entry point. That mode is reserved for an externally provisioned development service named `OpenBladeRustReadOnlyDev` and the fixed pipe `OpenBlade.Rust.ReadOnly.Dev.scm`. It uses the same account/logon admission, so a regular desktop client should be denied across the service logon boundary. The development executable does not provision a service.

The Rust workflow runs the native lifecycle smoke only on GitHub-hosted Windows VMs for same-repository pull requests. It checks a clean namespace, stages a hash-verified copy of the tested binary, and registers the demand-start service as LocalService. It verifies that a pre-existing pipe causes startup to fail, then observes RUNNING with an available listener and STOPPED with the pipe retired on two start/stop cycles. The runner's different-account pipe open must be denied. Cleanup stops and deletes the service and removes the staged binary.

The foreground interoperability suite still owns admitted-client and in-flight connection coverage. The hosted service smoke uses a separate account and does not test an admitted service-logon subscriber during SCM STOP or establish product multi-user policy. A test requiring those conditions needs a separately authorized disposable Windows VM.
