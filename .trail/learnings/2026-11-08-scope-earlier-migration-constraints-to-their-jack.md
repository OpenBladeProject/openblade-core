---
title: Scope earlier migration constraints to their owning libraries
author: Jack
date: 2026-11-08
branch: feat/rust-status-cli
touches:
  - ".github/workflows/rust.yml"
  - docs/rust-boundaries.md
  - rust/Cargo.lock
  - rust/Cargo.toml
  - rust/Check-Workspace.ps1
  - rust/README.md
  - rust/Test-WorkspaceCheck.ps1
  - rust/crates/openblade-status-cli/Cargo.toml
  - rust/crates/openblade-status-cli/README.md
  - rust/crates/openblade-status-cli/fixtures/README.md
---

## Decided
The next authorized Rust step adds a separate developer std CLI with JSON/runtime dependencies only at the outer status transport. It does not extend stage1-7 pure library responsibilities or change product version 0.57.1.

## Tried and ruled out
Trail check's lexical matches against earlier migration constraints do not forbid this newly authorized CLI. The v78 mention documents why that path is not used. No Console.In reader, full ServiceStatusSnapshot deserialization, battery optimizer, model admission, raw HID, firmware command, copied parity expectation, installed-product activation or version pin change is introduced. The binary target count of one is unrelated to physical fan targets.

## Watch out
Workspace guard mutation tests continue to verify manifest policy rejection, not compiler mutation. Fixed status fixtures are hand-authored independently; C# serialization checks the request. Explain these scope distinctions in the PR/commit when Trail's broad lexical check reports them.
