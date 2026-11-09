---
title: Preserve report validation precedence in the pure Rust codec
author: Jack
date: 2026-11-08
branch: feat/rust-report-codec
touches:
  - ".github/workflows/rust.yml"
  - docs/rust-boundaries.md
  - rust/Cargo.lock
  - rust/Check-Workspace.ps1
  - rust/README.md
  - rust/Test-WorkspaceCheck.ps1
  - rust/crates/openblade-protocol/Cargo.toml
  - rust/crates/openblade-protocol/src/lib.rs
  - rust/crates/openblade-types/src/lib.rs
  - rust/crates/openblade-protocol/tests/report_parity.rs
---

## Decided
Stage 3 extracts only the pure 90-byte report helper into no_std Rust. Keep the existing C# validator order, explicit diagnostic values0..8, signed optional expected lengths, accepted reserved/trailing bytes and owned payload copies. Encoding rejects payload size before exact destination length and publishes no output on failure. Tests include the canonical29 report cases directly from tests/Fixtures/Parity/v1/razer-report.json; serde and serde_json are pinned dev-only parser roots. The inward runtime graph remains registry-free and unsafe is forbidden.

## Tried and ruled out
Do not copy or regenerate golden fixtures, handwrite a JSON parser, add a transport/session, allocate transaction IDs, expose raw firmware commands, activate a native ABI or change production C#/schemas. The14 profile vectors remain stage6. Bounded deterministic fuzz/property tests cover malformed envelopes without a new fuzz toolchain or parser runtime dependency.

## Watch out
Nullable fixture fields are required even when null; serde Option alone would accept absence. Use deserialize_with and negative tests for every field, duplicate fields/IDs, provenance, numeric strings and uppercase hex. Keep cargo fetch --locked before offline guard checks in CI. Stage3 admits only the two protocol test targets and reviewed serde closure; future stages must evolve guards deliberately. Synthetic parity is not physical validation. Product version impact is none because no shipped behavior or payload changes.
