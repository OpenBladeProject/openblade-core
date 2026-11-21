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
the current Windows desktop. It uses the locked `windows-sys` SDK bindings
and has no production caller. Its tests script native responses.

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
