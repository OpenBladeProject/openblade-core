# Rust workspace

Phase 2 adds buildable crate boundaries and native CI. The Windows product
continues to use C#; its build, installer and release payload do not consume this
workspace. These crates contain no algorithms, tests or C exports yet. The FFI
crate builds as an ordinary Rust library, with no native DLL artifact.

## Develop

Install [rustup](https://rustup.rs/) and the platform's native C/C++ linker
(MSVC Build Tools on Windows, Xcode Command Line Tools on macOS, or a C toolchain
on Linux). From `rust/`, run:

```text
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
on November 7, 2026. The edition is 2024 and the initial MSRV equals the tested
pin; no older compiler compatibility is claimed. Update the pin and workspace
`rust-version` together. `0.0.0` identifies unpublished scaffolding independently
of the Windows product version. Licensing follows the repository's
`GPL-3.0-or-later` policy. Commit `Cargo.lock`; there are no registry dependencies.

The [reviewed boundaries](../docs/rust-boundaries.md) define the inward graph:
protocol and domain depend on types; FFI depends on all three. The workspace
check rejects extra packages, changed dependency edges, non-library targets,
MSRV drift and removal of the unsafe/no_std policies. It resolves its directory
independently of the caller. Guard regression tests use disposable workspace copies.
The three inner crates use `no_std`, and all crates inherit a compiler-enforced
unsafe-code prohibition. Platform APIs and hardware access remain host work.

## Validation scope

[The Rust workflow](../.github/workflows/rust.yml) runs build, tests, formatting,
strict clippy and the metadata boundary check on these native runners:

| Runner | Target |
| --- | --- |
| Windows 2025 x64 | `x86_64-pc-windows-msvc` |
| Ubuntu 24.04 x64 | `x86_64-unknown-linux-gnu` |
| macOS 15 Intel | `x86_64-apple-darwin` |
| macOS 15 ARM64 | `aarch64-apple-darwin` |

Runner labels follow [GitHub's hosted-runner reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
An explicit host check ensures the target is executed natively. Windows ARM64,
Linux ARM64, musl, 32-bit and other targets are not validated in this phase.
The empty crates intentionally run zero behavioral tests; green CI establishes
workspace/toolchain portability, not protocol parity, OS integration or hardware
support. This workflow publishes no artifacts and accepts stacked pull requests.
The existing Windows CI keeps its conservative full-validation fallback for
unknown paths, including Rust; no .NET or packaging gate is bypassed.

PR3 will implement the pure helpers and consume the single canonical
[parity fixture set](../tests/Fixtures/Parity/README.md) (43 C# baseline vectors:
9 encoding, 20 response and 14 profile cases).
Do not copy or regenerate those fixtures here. C ABI implementation, C# native
loading, runtime selection and later OS adapters remain separate stages. Product
SemVer, IPC versions and saved schemas are unchanged by this scaffold.
