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

The first process initially reports an 80 percent requested charge limit. The commit and the restarted process report 75 percent. Both use the existing synthetic recovery reducer with an unavailable getter result. Confirmed battery state stays unknown, writes stay blocked, and v51 keeps its passive fixture projection. The standard input command advances this fixture only. The pipe still refuses mutations.

The process suite checks the managed documents byte for byte, keeps the original marker visible after interruption, verifies backup and stage contents, and compares every retained artifact before and after refusal. It also rejects a changed root identity, a path argument and a directory junction. Successful runs end on standard input EOF. The managed harness removes its fixture after the processes exit. If driving the modes directly, use a fresh pipe ID each time and remove the reported fixture directory after inspecting the result.

The executable can write only a newly created fixture root. It derives the namespace from the Windows known folder on a fixed drive, pins plain directory components, and uses fixed filenames and embedded payloads. The read-only restart gate accepts only those complete managed aggregates. This developer boundary relies on the test account controlling its temporary files. Production bootstrap and migrations still need typed domain validation, protected path and ACL admission, coherent snapshots and cross-process ownership, specialty journal integration, operator recovery, and native owner takeover gates. The interruption is injected and provides no evidence about sudden power loss.

## Compatibility boundary

- Each connection accepts one v79 `getStatus` or `subscribeStatus` request, or one v51 `getStatus` request. The v79 response and event use the [managed replay fixtures](windows-backend-replay.md). In the fixed replay modes, the subscription first sends the preceding synthetic revision and then one exact newer fixture event. v51 returns only the CLI fixture's approved fields and closes. Request IDs correlate replies. Every pipe read uses a cached snapshot and has no activation or state changes.
- Framing uses a signed 32-bit little-endian byte count and caps bodies at 64 KiB. There is a two-second first-frame deadline and a two-second deadline per write. Invalid lengths, malformed envelopes, invalid identity and unsupported versions close the connection. Other operations receive a correlated refusal before any dispatch. The host allows eight concurrent handlers and four concurrent subscriptions, with one additional listening instance and a private first-instance claim. The host promptly closes connections above capacity and retries a temporarily busy listener replacement for up to two seconds.
- Windows claims the first instance exclusively and rejects remote connections. The explicit DACL restricts access to the host account. After reading the request bytes, the host resolves the pipe token and requires the same SID, logon authentication ID and Windows session. This is a same-logon development trust boundary. The ordinary duplex C# client asks for generic write, which also gives its SID permission to create pipe instances. It does not establish server trust against another process in that logon. Both projections exercise synthetic authorization inputs behind the native identity check. They do not replicate active-session selection or LocalSystem access policy.
- Rust tests cover the wire fixtures and native endpoint checks, including first-instance contention, listener saturation and recovery, an anonymous client, and start/stop ordering through the shared lifecycle with an injected SCM reporter and stop signal. The .NET tests exercise the real tray client through the foreground endpoint, including initial status, event, cancellation and reconnect. They supplement that path with managed codec and v51 raw frames. The Windows job in the Rust workflow runs both sets on stacked PRs.

## Native SCM gate

The executable links the native dispatcher and registers the stop/shutdown handler from its `--scm` entry point. That mode is reserved for an externally provisioned development service named `OpenBladeRustReadOnlyDev` and the fixed pipe `OpenBlade.Rust.ReadOnly.Dev.scm`. It uses the same account/logon admission, so a regular desktop client should be denied across the service logon boundary. The development executable does not provision a service.

The Rust workflow runs the native lifecycle smoke only on GitHub-hosted Windows VMs for same-repository pull requests. It checks a clean namespace, stages a hash-verified copy of the tested binary, and registers the demand-start service as LocalService. It verifies that a pre-existing pipe causes startup to fail, then observes RUNNING with an available listener and STOPPED with the pipe retired on two start/stop cycles. The runner's different-account pipe open must be denied. Cleanup stops and deletes the service and removes the staged binary.

The foreground interoperability suite still owns admitted-client and in-flight connection coverage. The hosted service smoke uses a separate account and does not test an admitted service-logon subscriber during SCM STOP or establish product multi-user policy. A test requiring those conditions needs a separately authorized disposable Windows VM.
