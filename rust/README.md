# Rust workspace

The Windows host uses the Rust report codec, decoded profile merge and bounded automation selection from the
shared core. The required native component is built with ordinary
.NET builds and tests, and packaged with the coordinated product payload.
Hardware ownership, admission, IPC and saved schemas remain in the host.

## Develop

Install [rustup](https://rustup.rs/) and the platform's native C/C++ linker
(MSVC Build Tools on Windows, Xcode Command Line Tools on macOS, or a C toolchain
on Linux). Windows builds require the x64 C++ tools and a Windows SDK.
Ordinary Windows builds use Windows PowerShell 5.1; native CI also uses
PowerShell 7. Add rustup's Cargo bin directory to `PATH`. From `rust/`, run:

```text
cargo fetch --locked
cargo build --locked --workspace --all-targets
cargo test --locked --workspace --all-targets
cargo fmt --all --check
cargo clippy --locked --workspace --all-targets -- -D warnings
pwsh -NoProfile -File ./Check-Workspace.ps1
pwsh -NoProfile -File ./Test-WorkspaceCheck.ps1
```

`rust-toolchain.toml` selects Rust **1.99.1**, rustfmt and clippy. This stable
release was verified against the [official release announcement](https://blog.rust-lang.org/2026/10/15/Rust-1.99.1/)
and [stable manifest](https://static.rust-lang.org/dist/channel-rust-stable.toml)
on November 7, 2026. The edition is 2024 and the MSRV equals the tested pin;
no older compiler compatibility is claimed. Update both together. `0.0.0`
identifies these unpublished migration crates independently of the Windows
product version. Licensing follows `GPL-3.0-or-later`.

Commit `Cargo.lock`. The protocol and domain integration tests use registry roots:
exact `serde` 1.0.228 with derive and `serde_json` 1.0.149, plus their locked
parsing/proc-macro dependency closure. These established parsers provide strict
typed JSON and duplicate-field rejection without adding a handwritten parser.
The separate developer-only `openblade-status-cli` uses the same JSON parsers
and Tokio 1.54.1 for bounded asynchronous Windows pipe I/O and cancellation.
Its locked OS/runtime dependencies stay in the CLI. Production libraries have
only inward local dependencies. See [the read-only status guide](crates/openblade-status-cli/README.md).
The isolated `openblade-platform-windows` crate reads active internal display
topology, identity and the distinct desktop and physical refresh rates from
the current Windows desktop. Its read-only refresh projection also reads GDI
modes and target-bound preferred timing and EDID evidence for the managed v2
QueryDisplay options and current key. The HDR read records support, the user
preference and bits per channel using the target-bound INFO_2 packet. The
crate uses locked Windows SDK bindings and has no production caller.

### Private Windows Session Agent replay

The development executable in the separate Windows host workspace enables
the scripted acquisition feature. Its private current-user pipe runs the
v2 query handler and projects replies from synthetic CCD, GDI and INFO_2
packets through the same platform acquisition logic as the native adapters.
Each request acquires a fresh synthetic snapshot from the fixture state. The fixed 60 Hz desktop and 240 Hz
signal with boost off yields two fixed options and an unset current key.
The HDR result represents an enabled user preference and 10 bits per channel.
A changed target between observations fails the query, as do a CCD status
error and an INFO_2 status error. Empty topology succeeds with unavailable
state.

Run the pipe interoperability checks on Windows from the repository root.

```powershell
cargo build --offline --locked --manifest-path tools/openblade-windows-host/Cargo.toml --bin openblade-rust-session-agent
$env:OPENBLADE_RUST_SESSION_AGENT_EXE = (Resolve-Path 'tools/openblade-windows-host/target/debug/openblade-rust-session-agent.exe').Path
dotnet restore src/OpenBlade.Tray/OpenBlade.Tray.csproj
dotnet test tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj --filter FullyQualifiedName~RustSessionAgentTests
```

The managed Display tests check the shared refresh and HDR input oracles.
The Session tests check the v2 envelopes, including a successful query with
unavailable state. The private query includes brightness and supported levels
only when the synthetic WMI reading matches the CCD endpoints and monitor path.
Unavailable brightness leaves the independently observed display fields intact.
ICC profiles are available only in the scripted ICC scenarios.

The private display endpoint accepts query and the existing typed brightness,
HDR and refresh-rate requests. Each process starts one scripted brightness worker; no COM policy is
applied to Windows and no native display or WMI transport is started. A confirmed
write requires a separate read from the full submitted WMI instance. A lost or
ambiguous write reply pauses all display mutations. Read-only observations
remain available when the worker drains. A native host still needs process-wide
COM security and confirmed current-user session and power evidence.

Phase 6s reuses v2 SetHdr through the actual Tray client. The serialized fixture
captures a scripted active session and power generation, checks the exact
single internal source, target and monitor path again, and verifies HDR support
and the current preference before deciding whether a write is needed. After
one typed submission, a separate INFO_2 read must confirm the same panel and
the requested preference. An uncertain result, readback or reply pauses all
display mutation paths for that fixture process. A later query can observe
recovery without clearing the pause or replaying the request. The fixture
keeps the HDR preference independent of signal mode and does not acquire SDR
profiles in either state.

The packet layout and command type follow the
[Microsoft SDK header](https://raw.githubusercontent.com/microsoft/win32metadata/main/generation/WinSDK/RecompiledIdlHeaders/um/wingdi.h)
and [DisplayConfigSetDeviceInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-displayconfigsetdeviceinfo).
The platform unit test links the native setter only to check packet encoding;
it retains the function pointer without executing the call. Production builds
with the `display-hdr-write` feature call it once, from `hdr::set_internal_hdr`
after the host fence admits the submission; `HdrNative::submit` is public, so a
dependent must not call it outside that sequence.

Phase 6t admits the existing v2 SetRefreshRate request through the Tray client.
The fixture checks option membership against the initial and fresh snapshots.
A no-op requires a fresh match on the desktop rate, physical signal rate and
boost bit. Packet preparation uses paired CCD arrays, retains the source and
virtual desktop indices, and changes only the chosen path and its target timing.
A fixed rate without a known target timing omits the target index so Windows can
select it. A Dynamic rate requires an admitted physical timing. Scripted
validation and application use the corresponding
[SetDisplayConfig flags](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setdisplayconfig).
The fixture requires separate CCD readback of the exact panel and mode and pauses
all display writes on uncertain submission, readback or reply. Platform unit
tests retain the refresh setter import without calling it. Production builds
with the `display-refresh-write` feature share the same packet preparation and
call `SetDisplayConfig` only from `refresh_write::set_internal_refresh`: once
with `SDC_VALIDATE`, then, after the host fence admits it, once with
`SDC_APPLY`. A fixed rate supplies the panel's exact target timing when one is
known; success needs a readback that matches both the desktop and the signal
rate. `RefreshNative::apply` is public, so a dependent must not call it
outside that sequence.

Phase 6v extends the private fence across process restarts. `foreground` uses
a fresh pipe ID and creates its matching fixture directory under the process's
local temp directory. `resume-display` uses the same ID and temp directory
and retains pending intent across ordinary queries.
`reconcile-display` explicitly authorizes a fresh query to retire the owned
intent after the old fixture process has exited. The replacement process must
claim the same private endpoint, match its logon and session, and observe the
exact panel at the recorded baseline or requested value. The simulator starts
with its scripted values after each launch, so that read establishes the state
of the replacement fixture only. Missing, invalid or unreadable evidence
leaves writes paused and queries available. Recovery never replays a setter.
The integration tests own and remove their scratch directories.

Phase 6x extends the same fence to typed v2 SetColorProfile through the
managed Tray adapter. The scripted WCS transaction records the exact panel
and monitor key, SDR preference, selected scope, and the defaults and
associations in both scopes before any possible submission. A confirmed
selection or restoration needs a separate complete WCS observation and a
delivered reply to clear its intent in the original process. An explicit
recovery query after process exit can settle the saved baseline or the
complete expected selection. The displayed profile ID alone cannot settle
intent. While intent is unresolved, all four display setters remain fenced.

The installed Agent's `agent-display-color-write-v1` build answers manual
setColorProfile with the managed transaction. `color_profile::write` reads the
panel, HDR state, listed profiles and the current-user scope, default and
associations, then refuses HDR on, unknown HDR, an unlisted profile or a
changed panel before any submission. It adds a missing association, selects the
current-user scope and sets the default, confirming each step by a separate
read. After a submitted step fails it reconciles, then restores the default,
removes the added association and restores the scope, in the managed order,
without retrying the failed setter. The host fence admits the first setter,
and also requires confirmed SDR there. A failure after it pauses
color-profile writes only, until an explicit display read; a readback that
cannot re-identify the panel or read its HDR state is reported without
restoration writes. Platform unit
tests retain the WCS imports without calling them; production builds with
`display-color-write` call them only from `set_internal_color_profile`, so a
dependent must not call `ColorProfileNative` outside that sequence.

The installed Agent's `agent-display-profile-apply-v1` build answers manual
applyProfile (`session-display-profile-apply-partial-v1`). It selects all four
display writes and adds no setter: each step is the reviewed single-field
attempt with its own fence. One checkpoint covers every configured field before
any device interaction. An unconfigured profile is one query without power
admission. Otherwise the fields are taken in the managed order and with the
managed conditions (refresh rate, HDR when supported, color profile under SDR
ignoring case, the nearest brightness level). The selecting query decides the
first field and each later field is decided from the previous step's reply
query, never from an unavailable one; a successful step whose power context
changed is stale. The first step that does not succeed ends the profile, and
the final reply must show every requested setting. The durable record keeps
the managed semantics, while the native write gate pauses exactly the fields
that may have an unresolved submission or that the final reply no longer
shows; any available read reopens the refresh, HDR and color gates, an HDR
read reporting no support included. No deviation writes a field that managed
would not write on the same display state. Deliberate deviations that report
more certainty than managed (Failed or Blocked where managed reports
Indeterminate), each only when nothing was submitted: a no-op step is not a
submission, so a later failure before any submission is Failed or Blocked; a
step's fence refusal before any submission is Blocked; a selecting read
interrupted by cancellation fails before any write; and a fatal query of an
unconfigured or power-refused profile is recorded Succeeded or Blocked. Other
deviations: the profile stops at a confirmed step its reply does not show;
the gate refuses the whole profile while any configured field is unresolved;
each later field is decided from native's fresh reply query rather than from
the setter's own result snapshot (managed `result.State`), so when they
differ it follows the managed rule on the current display state, and a
configured field facing an unavailable reply fails (Indeterminate after a
submission); and the request must be a settings object carrying every member
the managed codec writes, including the computed `isConfigured` (managed
requires `refreshRateKey`, `hdrEnabled` and `colorProfileKey` too, but also
accepts encodings it never writes, such as a missing `brightnessPercent` or
`isConfigured`, an inconsistent or null `isConfigured`, duplicate members,
other member-name casing and a string percent; it rejects an array like
native).

The installed Agent's `agent-display-automation-v1` build (marker
`session-display-automation-v1`) adds managed display automation: each
confirmed power source or application change applies the profile through the
same applyProfile composition, under the display operation gate that also
serializes every manual query and write. Both package readers refuse the
feature and the marker. See
[Native Agent display automation](../docs/windows-native-agent-display-automation.md)
for the managed inventory, the omissions and deviations, and the parity
vectors.

The `agent-power-mode-automation-v1` build (marker
`session-power-mode-automation-v1`) adds managed power-mode automation: each
confirmed performance preset sets the user's Windows power mode for the
current supply from the profile set's association, with one retry. The
`agent-session-power-relay-v1` build (marker `session-power-relay-v1`)
reports each session suspend and resume to the Service, as the managed relay
does, within the managed 1.5 s budget. Both package readers refuse both
features and both markers. See
[Native Agent power-mode automation](../docs/windows-native-agent-power-mode-automation.md)
and [Native Agent session power relay](../docs/windows-native-agent-session-power-relay.md).

Current HDR output mode, complete snapshot parity, and native display behavior
need separate evidence before replacement. Live refresh still needs authorized
validation of the exact packet on the active console, fresh power/session and
monitor identity checks around validation and submission, and recovery that
survives a host restart. Product, Session IPC and persisted schema versions are
unchanged by this replay.

**Restricted Claude review is pending direct user authorization.** Keep the
combined branch and its artifacts local. Native display validation,
production integration and release validation remain separate gates.

The same crate contains the WMI brightness backend logic behind
`WmiBrightnessNative`. Display settings supply a percentage and resolve it to
the nearest supported level, with ties going to the lower level. Function-row
actions use a ten-point directional step and report brightness only on a
confirmed change. The [managed brightness oracle](https://github.com/OpenBladeProject/openblade-core/pull/939)
pins the level, acknowledgement and readback cases.

The Rust path requires one CCD internal target and one matching active WMI
brightness row for both entry points. A write also requires one active method
row with the full instance identity, then fresh topology and supported-level
admission. It submits once. UInt32 zero or either Boolean acknowledgement
requires independent readback from the submitted instance at the selected level.
An acknowledged write with delayed confirmation can resolve through a later
read of the same panel and level. An uncertain native result keeps writes paused
even if a later read happens to match. Preparation, submission completion, and
readback have separate deadlines. Caller cancellation stops preparation and
cannot discard readback after possible submission.

This slice uses scripted WMI effects. The current-user WMI transport must enforce
the native deadline and submission contract before wiring the backend to the
Session Agent. Session power and lifecycle admission, IPC projection and held-key
routing belong to that integration. The timeout and stop behavior still need
native and supervised panel validation.

The same crate projects Session GPU inventory from scripted NVIDIA PDH and
process observations. It keeps idle engine holders, unavailable counters and
stable activity with missing or stale process identity distinct. App rows
require matching start time, user SID, session and name across both samples.
Grouping also requires stable parent evidence, and a foreign helper cannot
make a current-user parent eligible. The host supplies the managed ordinal
name comparison. Tests port examples from the managed GPU activity and Session
GPU dispatcher tests.

#### Private Windows Session GPU replay

The same development executable can run one GPU endpoint instead of Display:
`gpu-foreground <32 lowercase hex digits> <available|empty|no-data|large|stalled>`.
It claims a separate `OpenBlade.Rust.Session.Gpu.Dev.<id>` pipe for the
current user. It also checks the peer's session and logon before dispatch.
Use the same Windows build and managed interoperability test commands above.

The fixture supplies fixed observations to the Phase 6n sampler and projection.
It uses the managed v2 GPU request and response format. A successful empty
inventory is distinct from unavailable data; stable idle holders still appear
with zero utilization. Partial replies retain the ordered prefix that fits both
the row and frame limits. The endpoint accepts one request per connection.
Frame I/O has deadlines, and a disconnect, extra input, deadline or shutdown
cancels a pending scripted scan. The fixture drains a cancelled scan before
accepting another connection.

The private endpoint reports every row as unavailable for closing. The internal
inventory retains policy eligibility and its reasons for later use. Both normal
and forced close return a correlated failure before sampling. The host has no
process action or fallback on this endpoint.

Live GPU query and coherent parent metadata still need separate validation.
The Phase 6n native sampler does not supply a process parent table. This
fixture does not run the native sampler, inspect system processes or change the
installed product.

#### Installed Session GPU endpoint

The installed Agent's `agent-gpu-v1` build serves the managed Session GPU v2
endpoint. It uses the managed pipe name, ACL, deadlines, strings and caps.
Queries use the production sampler. Close and force close use the platform
`gpu-close` backend, which opens each confirmed row once and re-reads its
identity, owner and user on the handle it acts on. Stop leaves the remaining
targets unattempted and writes no reply, as managed does. As managed, close
and force close act only at or below the Agent's integrity level, and each
descendant stays held until its children are listed. Any endpoint ending stops
the Agent.

Deliberate differences from managed:

- Requests must use the managed-written encoding: camelCase names, JSON
  numbers, no duplicate members and `operation` first.
- A disconnect or an extra byte cancels the dispatch, like Stop.
- An app list beyond the 64 KiB frame keeps the largest prefix that fits.
  Managed fails the write.
- Handles use least-privilege access rights, so a hardened same-user process
  that denies `PROCESS_ALL_ACCESS` can still be force-closed.
- A root that exits before its integrity read reports a failure.
- A failed `WM_CLOSE` post is a failure.
- A force close succeeds only after the process is seen to exit within 5 s.
- Force close never falls back from the tree to the exact process.
  Descendants are terminated only after the root exits. Each must be in the
  current user's session and unprotected. At most 256 are visited.
- A failed name comparison refuses the submission.

[Native Session GPU endpoint](../docs/windows-native-session-gpu.md) lists the
complete differences, the offline evidence and the runtime validation still
needed. None of it has run against live counters, processes, windows or a real
pipe.


Its application reporter binding accepts an already observed foreground HWND.
It confirms that the window and its owner still belong to the active local
console and Default input desktop around image lookup. It retains the same
query/synchronize-only process handle for lifetime checks and treats unavailable
evidence and `ApplicationFrameHost.exe` as unknown. Pending foreground samples
coalesce to the newest one. No watcher, timer, exit callback or IPC is started;
the owner supplies observations, elapsed time and transport outcomes.

Executable identity uses bounded Win32 paths and .NET 10 ordinal casing:
system ICU for BMP characters and the managed uppercase ranges for
supplementary characters. Recheck parity if the managed runtime or Windows
ICU changes. Ordinary drive and UNC paths use
`GetFullPathNameW`; extended drive and UNC paths retain their literal spelling.
This binding declines nonstandard device namespaces, alternate data streams,
malformed UTF-16, paths beyond the fixed buffer, and ordinary names containing
`~` rather than attempt .NET's filesystem-dependent short-name expansion.
Ordinary long paths still depend on the eventual host's Win32 long-path
configuration. Managed NLS, invariant mode and app-local ICU overrides are
outside the checked comparison contract.

The same crate contains the current-user WindowsHook function-row adapter for
RZ09-0581 and RZ09-0528. It accepts canonical F1–F12 callback packets, uses a
single expiring host observation, and queues typed actions for a worker. The
first down for each key passes through until the hook observes that key's up.
The hook has desktop scope, so its packet cannot identify the originating
keyboard. Tests use inert packets and a scripted message loop. The native
Agent's `agent-function-row-v1` role wires it on RZ09-0581 only, from the
Service's confirmed Driver mode, without Fn observation or any effect yet; see
[Native Agent function row](../docs/windows-native-agent-function-row.md).
Independent hook health evidence and live Windows validation remain separate.

The standalone `openblade-domain::function_row` module consumes a single typed
observation per edge. It keeps Windows hook pairing and recovery separate from
Razer driver forwarding and retirement. RZ09-0484 driver strokes require fresh
host-managed Fn admission, independent of displayed Fn availability. Scripted
tests exercise the policy without input capture, injection, or action execution.

#### Private Windows Session Audio fixture

Run `audio-foreground <32 lowercase hex digits> <available|unavailable|stalled|modern|legacy|ambiguous>`
on the development Session Agent. It claims `OpenBlade.Rust.Session.Audio.Dev.<id>`
with the existing local-only, current-user pipe owner and checks the peer's
session and logon before dispatch. Close standard input to stop. The unified fixture accepts an explicit
`audio-available` option after its reporter ID, alone or together with
`battery-available`. It claims the Audio pipe before announcing readiness
and serves only the synthetic `available` reads.

The wire oracle is managed commit `6d6abc9ba28f88c102e295567f73cf11e3bbfabe`.
At that commit Audio uses protocol **v2**. The source pins are
[message definitions](../src/OpenBlade.Session/SessionAudioMessages.cs),
[codec](../src/OpenBlade.Session/SessionAudioIpcCodec.cs),
[dispatcher](../src/OpenBlade.SessionAgent/SessionAudioRequestDispatcher.cs),
[audio pipe policy](../src/OpenBlade.SessionAgent/SessionAudioPipeServer.cs) and
[shared pipe lifecycle](../src/OpenBlade.SessionAgent/SessionAgentPipeServer.cs).
The pinned [codec tests](../tests/OpenBlade.Session.Tests/SessionAudioIpcCodecTests.cs)
cover wire validation and diagnostic proof. The pinned
[dispatcher tests](../tests/OpenBlade.SessionAgent.Tests/SessionAudioRequestDispatcherTests.cs)
cover diagnostic provenance, fresh helper admission and the distinction between
explicit Read and Connect after an uncertain mutation.

The original three scenarios accept only `read` for Speakers and Headphones.
Requests use canonical managed member names, a nonempty GUID and the numeric
endpoint enum.
It uses a signed little-endian length and a 64 KiB UTF-8 JSON limit. One
connection serves one request. A successful response is `AvailableReadOnly`
with fixed synthetic state for the requested endpoint. The unavailable fixture
returns a correlated `Unavailable` result with a synthetic-provider error and
no state. Both omit `baseAudio`. The private endpoint and selected fixture
identify the source of the response. The response supplies no native proof.

Frame reads and writes each have two-second deadlines. A stalled headphone
read expires after ten seconds and retires only its connection without a
response. Host shutdown cancels the synthetic future and closes the listener.
A caller can cancel its own wait without producing a typed server result.
The synthetic future has no external work to drain.

The opt-in `modern`, `legacy`, and `ambiguous` scenarios also accept the managed
`connect` and `setSpatial` requests. All scenarios use the same private pipe and framing. The runtime
slice follows the same managed pin for
[compatibility](../src/OpenBlade.SessionAgent/ThxAudioCompatibility.cs),
[runtime admission](../src/OpenBlade.SessionAgent/ThxAudioAdmission.cs),
[modern helper](../src/OpenBlade.SessionAgent/ThxAudioHelperCommand.cs),
[legacy helper](../src/OpenBlade.SessionAgent/LegacyThxAudioCommand.cs), and
[worker results](../src/OpenBlade.SessionAgent/ThxAudioHelperProtocol.cs).
The [helper tests](../tests/OpenBlade.SessionAgent.Tests/ThxAudioHelperCommandTests.cs)
pin the modern two-output probe, pre-write admission, and post-write readback.

The stateful fixtures inject interactive host, exact model/USB/codec, controller,
trusted stack, and typed output evidence. The first modern read probes both
outputs and confirms only the requested one for dispatch. Legacy reads require
the exact active output and report EQ as observed only. Modern EQ and application
profiles are outside this slice. A spatial change requires a prior confirmation
for that output, fresh matching admission, a typed pre-read, and independent
same-runtime readback of the requested value. Stack and output changes invalidate
both endpoints' confirmations.

`ambiguous` applies the first change only to the inert worker's memory and then
reports `Indeterminate` with no state. Possible submission clears both
confirmations and blocks further mutations and Connect across connections.
A successful explicit Read restores confirmation only for its requested
output. Cancellation during possible submission and a failed response send
after confirmed submission also require reconciliation. The fixture retains
no state across process restarts.

Discover, EQ, presets, enhancement changes, application profiles, and
provisioning remain outside the private command set. Native parity still needs
actual Windows evidence collection, a supervised native worker, end-to-end
component and output verification, independent base-audio diagnostics, and
automation ordering. Production audio IPC and backend selection are unchanged.

Run the managed wire test with the private executable selected by
`OPENBLADE_RUST_SESSION_AGENT_EXE` and the filter
`FullyQualifiedName~AudioFixture_` on `OpenBlade.Tray.Tests`.

The workspace guard checks crate boundaries, reviewed targets and dependencies,
MSRV and compiler policies. Its regression tests use disposable workspace copies.
Inner crates retain `no_std` and inherit `unsafe_code = "forbid"`. FFI and Windows
platform unsafe operations are limited to their documented interop boundaries,
with `unsafe_op_in_unsafe_fn` denied. Both dev and release
profiles retain unwind panic handling; aborts and process faults are not caught.

## Report API

`openblade-types` defines encoding errors and explicit protocol-validation
numbers 0 through 8 matching C# diagnostics. `openblade-protocol` provides:

- `RazerReport::create`: copies a payload of at most 80 bytes into bounded storage.
- `encode` / `encode_into`, and static `encode_report`: produce exactly 90
  protocol bytes, excluding the OS report-ID prefix. Span encoders require an
  exact-length destination and leave it unchanged on error. Static encoding
  checks the payload size before the destination size.
- `calculate_checksum`: checks envelope length before XOR over offsets 2..=87.
- `validate_response`: checks length, checksum, transaction, command class,
  command ID, successful status and payload length in C# order. It returns an
  owned `ReportPayload`; the optional expected length remains signed to preserve
  rejection of negative C# contract lengths. Like C#, it accepts reserved bytes
  and the trailing byte when the checked fields are coherent.

The safe API uses ordinary Rust values and bounded arrays, with no allocation,
I/O, transaction allocation or hardware session. It neither admits firmware
commands nor performs model checks; those remain host responsibilities.
The [reviewed boundaries](../docs/rust-boundaries.md) define the ownership graph.
Service IPC, Session Agent IPC and saved schemas remain unchanged.

## Validation scope

[Native CI](../.github/workflows/rust.yml) runs locked builds/tests, formatting,
strict clippy and guard regression on these native runners:

| Runner | Target |
| --- | --- |
| Windows 2025 x64 | `x86_64-pc-windows-msvc` |
| Ubuntu 24.04 x64 | `x86_64-unknown-linux-gnu` |
| macOS 15 Intel | `x86_64-apple-darwin` |
| macOS 15 ARM64 | `aarch64-apple-darwin` |

Each lane reads the same canonical
[parity JSON](../tests/Fixtures/Parity/README.md) through `include_str!` from the
repository, without copied or regenerated expectations: 9 encoding and 20
response cases. Strict-reader negative tests reject malformed schema/provenance,
unknown/duplicate/missing fields and IDs, numeric strings and noncanonical hex.
Deterministic property tests exercise payload/destination bounds, copied
ownership, all combinations of envelope faults, signed lengths, checksum byte
coverage and a 20,000-input malformed corpus. These software checks establish
codec parity and portability; physical validation remains separate.

All 14 profile vectors run through Rust and the real native C# policy wrapper.
Production loading and runtime selection are described below. Windows ARM64,
Linux ARM64, musl and 32-bit targets are
unvalidated. CI publishes no artifacts and
accepts stacked PRs. Existing Windows CI retains its conservative full-validation
fallback for Rust changes.

## Developer C ABI and .NET interop

The [reviewed C header](crates/openblade-ffi/include/openblade.h) freezes ABI
`0x00010000` and the `ob_abi_version`, `ob_v1_encode_report` and
`ob_v1_validate_response` exports. `openblade-ffi` emits `rlib`, `cdylib` and
`staticlib` from the safe codec. Records use natural 4-byte alignment, explicit
sizes and fixed-width values; borrowed buffers never transfer ownership. Read
[the boundary contract](../docs/rust-boundaries.md#codec-c-abi-v1) before binding it.

Run the explicit developer test path from the repository root:

```powershell
pwsh -NoProfile -File rust/Test-NativeInterop.ps1
```

It builds the actual native library, compiles and runs the C header smoke test,
and runs `OpenBlade.Native.slnx`. Native tests require an absolute
`OPENBLADE_NATIVE_LIBRARY` path and fail if it is absent or unloadable. The four
native CI lanes run the same path. They test record layouts, all 29 canonical
report cases, all 14 profile cases and preservation sentinels, native buffer/error contracts, output integrity, concurrent calls
and module disposal. The two canonical destination-length cases concern the
existing exact-span C# API; the ABI separately admits any capacity at least 90.
The Rust boundary tests trigger private test-only panics through the real work
exports with ordinary and destructor-panicking payloads; no test export exists.
Caught payloads are deliberately forgotten to avoid a second unwind across C.
The process panic hook is unchanged and may emit diagnostics.

To run only .NET tests after explicitly building Rust, set
`OPENBLADE_NATIVE_LIBRARY` to the normalized absolute Rust artifact path and
`OPENBLADE_POLICY_ONLY_LIBRARY` to the same platform filename in its `policy-only/`
subfolder (built by `Test-NativeInterop.ps1`), then run:

```text
dotnet test OpenBlade.Native.slnx -c Release
```

Debug artifact filenames are `openblade_ffi.dll`, `libopenblade_ffi.so`, and
`libopenblade_ffi.dylib` below `rust/target/<native-target>/debug/`. The C# wrapper
uses explicit Cdecl function pointers with SafeHandle lifetime references so
independent module instances and concurrent disposal are safe. It checks the
exact ABI, managed record layout and every returned status/error/length before
exposing owned output. It has no reference to the Windows-only Protocol assembly
and returns typed boundary errors to the host adapter.

The developer solution is separate from `OpenBlade.slnx`. Ordinary product builds
also build the required native component, as described under [production activation](#production-activation).
These tests establish software ABI parity on their runners and do not establish
physical hardware validation.

## Release payload and shadow comparison

On Windows x64, `build/Publish.ps1` builds the committed native source with the
pinned Rust toolchain and x64 MSVC/Windows SDK tools. Run `cargo fetch --locked`
from `rust/` first. The DLL uses static CRT linkage; packaging checks its actual
PE imports, file version and source-bound hashes. The existing signing switch
signs it before compiling the managed hash binding and embedding the installer.
See the [packaging contract](../docs/rust-boundaries.md#dormant-windows-packaging-stage-5)
for provenance, notices and upgrade behavior, and the [active product contract](../docs/rust-boundaries.md#active-codec-and-selected-profile-merge-stage-7)
for mandatory loading. Installed and developer payloads carry the native DLL,
provenance and notices.

Run the pure comparison against a trusted, explicitly built Windows DLL:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File rust/Compare-Shadow.ps1 -LibraryPath C:\path\to\openblade_ffi.dll
```

This compares the frozen independent managed baseline with native results for
the 9 encoding and 20 response fixtures. It performs no device discovery or
hardware I/O. Packaging reuses its selected configuration and native payload,
then verifies the final signed/unsigned artifact and exercises real payload
tampering checks. The separate four-platform ABI
lanes continue to test their native developer artifacts.

## Profile API

The allocation-free domain merge and separately negotiated additive policy ABI
are documented in [the stage 6 contract](../docs/rust-boundaries.md#dormant-profile-projection-stage-6).
Developer callers can load `NativeProfilePolicy` with an explicit artifact;
production uses `ProductNativeRuntime.Policy`. C# retains whole-profile validation,
host-only fields and hardware ownership. Codec v1 loading remains independent of
policy exports. IPC, saved schemas, firmware commands and product behavior are unchanged.

## Production activation

`OpenBlade.Protocol.RazerReport` delegates its pure encoding and validation to
`ProductNativeRuntime.Codec`. C# retains the exact 90-byte destination contract,
validation exceptions and messages. The host still owns the report-ID prefix,
transaction IDs, payload callbacks, batches and cancellation. Span encoding uses
bounded stack storage and publishes only a complete successful result.

`ApplicationProfileResolution` uses the selected Rust merge policy. Host-only
fields, fan curves and original key strings remain host objects. At most four
one-byte source tokens cross the UTF-8 ABI; this preserves arbitrary legacy
UTF-16 strings, including unpaired surrogates, without fallback or truncation.

Ordinary builds run `build/Build-NativeDevelopment.ps1` through the native
project. The output is local to that project's `obj/native/<configuration>`.
Release publication supplies the independently verified staged payload instead;
it signs the native DLL before compiling the managed hash binding. The native
project embeds the build manifest and copies the library transitively to each
consumer. A missing compiler, linker, library or manifest fails the build.

At startup Service, Capture and FirmwareSmoke admit the fixed native component
before laptop-control work. The assembly-embedded manifest binds target, version, ABI and
SHA-256. The loader uses the application base directory, verifies the file before
loading, and retains its file lease and modules through process exit so shutdown
cleanup can still encode handback commands. Failures have bounded numeric,
path-free diagnostics and require repair/rebuild. No legacy product fallback or
command replay is provided. Unrecoverable policy failures propagate through service
background supervision, receive a fixed critical diagnostic/nonzero exit status,
and retain bounded shutdown cleanup.

The managed report and profile implementations frozen at predecessor
`69f66ba673c0523c0521b09b286a7d09f8137488` live only in tests. Canonical fixture
expectations and provenance remain unchanged. These independent oracles continue
to check the activated path; they do not constitute physical validation.

On Windows, `rust/Test-ProductionActivation.ps1` runs four isolated offline
processes against the real admitted component. They verify a codec failure after
a mocked SET in the batch and response paths, and profile/automation policy failure with preserved
null/legacy validation branches. All HID handles and I/O are inert test doubles.


## Automation selection

`openblade-domain::automation` owns bounded performance-priority and lighting
origin/provenance decisions. The independently negotiated automation ABI v1 returns
only tokens for original host values. `NativeAutomationPolicy` binds the exports;
production uses `ProductNativeRuntime.Automation` under the existing startup
hash admission and shutdown lifetime. See [stage 8](../docs/rust-boundaries.md#automation-selection-stage-8)
for the exact boundary and independent oracle. Codec/profile ABI v1 consumers
remain compatible.

The native tests add 18 shared cases and compare every presence combination with
the frozen managed baseline. `Test-NativeInterop.ps1` also supplies
`OPENBLADE_AUTOMATION_ONLY_LIBRARY` from the artifact's `automation-only/`
subfolder for independent-loader tests. Set that path alongside the other two
native-library variables when running the managed suite directly.

`Test-ProductionActivation.ps1` now runs four isolated offline scenarios,
including automation-policy lifetime failure with host validation preserved.
All device resources remain inert test doubles.


## macOS discovery

The [developer discovery pilot](../tools/openblade-discovery/README.md) uses a
separate Cargo workspace and reads macOS metadata without opening a HID device
or sending reports.
