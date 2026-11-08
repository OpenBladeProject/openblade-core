# Rust workspace

Stage 6 adds dormant profile merge exports and C# mapping to the packaged Windows
DLL. Stage 5 established the codec payload and explicit developer shadow
comparison. The Windows product continues to use C#; ordinary .NET builds and
tests remain independent of Rust. Explicit release packaging builds Rust.

## Develop

Install [rustup](https://rustup.rs/) and the platform's native C/C++ linker
(MSVC Build Tools on Windows, Xcode Command Line Tools on macOS, or a C toolchain
on Linux). From `rust/`, run:

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

Commit `Cargo.lock`. Only the protocol and domain integration tests use registry roots:
exact `serde` 1.0.228 with derive and `serde_json` 1.0.149, plus their locked
parsing/proc-macro dependency closure. These established parsers provide strict
typed JSON and duplicate-field rejection without adding a handwritten parser.
Production libraries have only inward local dependencies. The workspace guard
checks the four libraries, reviewed test targets/dependencies, MSRV and compiler
policies independently of the caller's directory. Its regression tests use
disposable workspace copies. Inner crates retain `no_std` and inherit
`unsafe_code = "forbid"`. Only the FFI
crate permits the documented pointer/export boundary, with unsafe operations
explicitly marked and `unsafe_op_in_unsafe_fn` denied. Both dev and release
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
Production loading, runtime selection
and OS adapters remain later stages. Windows ARM64, Linux
ARM64, musl and 32-bit targets are unvalidated. CI publishes no artifacts and
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
and returns typed boundary errors for a later production host adapter.

The developer solution is separate from `OpenBlade.slnx`; ordinary product builds
do not invoke Cargo or load a Rust module. These tests establish software ABI
parity on their runners and do not establish physical hardware validation.

## Release payload and shadow comparison

On Windows x64, `build/Publish.ps1` builds the committed native source with the
pinned Rust toolchain and x64 MSVC/Windows SDK tools. Run `cargo fetch --locked`
from `rust/` first. The DLL uses static CRT linkage; packaging checks its actual
PE imports, file version and source-bound hashes. The existing signing switch
signs it before installer embedding. See the [packaging contract](../docs/rust-boundaries.md#dormant-windows-packaging-stage-5)
for provenance, notices and upgrade behavior. Product processes do not load it.

Run the pure comparison against a trusted, explicitly built Windows DLL:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File rust/Compare-Shadow.ps1 -LibraryPath C:\path\to\openblade_ffi.dll
```

This compares actual C# and native results for the 9 encoding and 20 response
fixtures. It performs no device discovery or hardware I/O. Packaging runs it
against the new Release DLL, then verifies the final signed/unsigned artifact
and exercises real payload tampering checks. The separate four-platform ABI
lanes continue to test their native developer artifacts.

## Profile API

The allocation-free domain merge and separately negotiated additive policy ABI
are documented in [the stage 6 contract](../docs/rust-boundaries.md#dormant-profile-projection-stage-6).
Use NativeProfilePolicy explicitly with a developer artifact. C# retains full
profile validation and all production paths. Codec v1 loading remains independent
of policy exports. No IPC, saved schema, hardware command or product behavior changes.
