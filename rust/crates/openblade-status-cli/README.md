# Read-only status prototype

From `rust/`, build with `cargo build --locked -p openblade-status-cli`,
then run `target/debug/openblade-status-cli.exe` as the normal current user.
The executable takes no arguments and makes one typed `GetStatus` request.
It is a developer tool, excluded from product packaging.

The integrated base is `dfda7baa4b0ef8210b1b72f24ab321ed113f1da1`
(product 0.57.1). Current service IPC is **78**. This tool deliberately selects
the service's existing **51 read-only compatibility** contract, on
`\\.\pipe\OpenBlade.Service.v2`. Current-version GetStatus may activate a
user configuration, including profile recovery; compatibility GetStatus bypasses
activation. The compatibility connection briefly pauses managed thermal
convergence admission and leaves it awaiting fresh observation when closed.
This is an existing service lease, not a firmware command.

The server retains its DACL, token SID/session resolution and interactive-session
authority. Tokio opens the fixed local pipe with Identification QoS, which the
server explicitly supports. No claimed user/session identity is sent.
The CLI inherits the existing client's local pipe trust model; a successful
response is service-reported status, not independent manufacturer, product or
control-HID discovery.

A successful output is a bounded JSON whitelist: response presence, negotiated
protocol, service-reported device verification, revision, numeric power/session
access, and five nullable readings. Power numbers retain 0=PluggedIn, 1=Battery,
2=UsbC, 3=ExternalUnknown; session access retains 0=Active, 1=InactiveSession,
2=NotInteractive. Unknown numeric enum values remain numeric. Negative battery
flow and null readings stay intact. Null/missing readings do not become zero.

The response is an existing cached service snapshot. It carries no telemetry
sample timestamp, freshness or sensor-provider provenance, so output labels
freshness **unknown** and source **cached-service-snapshot**. A response proves
that the endpoint responded successfully; it does not prove healthy hardware.
DeviceVerified=false alone does not establish an unsupported laptop. The v51
projection omits/changes unsupported-laptop, write-block, message, observation,
experimental and HyperBoost fields; none are exposed. Profiles, paths, serials,
applications, remote error text and arbitrary extensions are also excluded.

Frames use a signed four-byte little-endian length, 1..65536 UTF-8 bytes.
The client checks response kind, ID and exact negotiated version, rejects malformed
JSON and duplicate keys recursively (including ignored objects and Unicode case
aliases), then validates
the typed whitelist and the C# telemetry bounds. Unicode uppercase folding is
conservative for expanding case mappings; unique unknown fields remain compatible.
Each frame read/write has a two-second timeout; connection, exchange and Ctrl+C
cancellation share a five-second overall deadline. Only busy-pipe opens retry,
before any submission. There is no reconnect or request replay. Tokio owns the
handle and overlapped storage, including cancellation/drop.

Exit codes: 0 = successful validated summary, 1 = fixed redacted failure category,
2 = invalid arguments. A failure does not invent device or service-absence state.
Non-Windows builds return an explicit unsupported-transport failure.

`src/wire.rs` owns the closed DTO, strict decoding, framing and summary.
`src/transport/windows.rs` owns connection and runtime lifetime;
`transport/mod.rs` is the small replacement seam for a separately reviewed future
OS transport. There is no HID backend or generic control API.

The CLI alone uses exact Serde 1.0.228/serde_json 1.0.149 and Tokio 1.54.1.
Serde provides grammar/type checks; its visitor adds recursive duplicate rejection.
Tokio supplies safe partial asynchronous pipe I/O, deadlines and Ctrl+C handling,
avoiding a new unsafe Win32 adapter. Its locked OS/runtime closure stays outside
the four shared libraries. The inner no_std/unsafe/dependency boundaries are unchanged.
See [fixtures](fixtures/README.md) for independent expectations.
