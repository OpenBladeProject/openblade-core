# Rust workspace

Stage 3 implements pure report creation, encoding, checksum and response
validation. The Windows product continues to use C#; its build, installer and
release payload do not consume this workspace. The FFI crate remains an ordinary
Rust library without C exports or a native DLL artifact.

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

Commit `Cargo.lock`. Only the protocol integration tests use registry roots:
exact `serde` 1.0.228 with derive and `serde_json` 1.0.149, plus their locked
parsing/proc-macro dependency closure. These established parsers provide strict
typed JSON and duplicate-field rejection without adding a handwritten parser.
Production libraries have only inward local dependencies. The workspace guard
checks the four libraries, reviewed test targets/dependencies, MSRV and compiler
policies independently of the caller's directory. Its regression tests use
disposable workspace copies. Inner crates retain `no_std`; all crates inherit
`unsafe_code = "forbid"`.

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
No ABI, IPC or saved schema changes accompany this stage.

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

The 14 profile vectors await stage 6. C ABI implementation, C# loading, runtime
selection, packaging and OS adapters remain later stages. Windows ARM64, Linux
ARM64, musl and 32-bit targets are unvalidated. CI publishes no artifacts and
accepts stacked PRs. Existing Windows CI retains its conservative full-validation
fallback for Rust changes.
