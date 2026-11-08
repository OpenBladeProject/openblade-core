---
title: Keep the Rust status prototype on cached read-only IPC
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
The developer-only Rust status CLI uses the existing v51 read-only compatibility GetStatus on the v78 service at dfda7baa. The current-version path can activate a user configuration; v51 skips activation. Its temporary compatibility lease closes managed thermal-convergence admission and leaves it awaiting fresh observation at disconnect. Only unchanged whitelist status fields are displayed.

## Tried and ruled out
Do not call ObserveState for the first probe or use v78 GetStatus as a guaranteed passive read. Do not expose compatibility message/writesBlocked, unsupportedLaptop or HyperBoost confirmation: the projection substitutes or omits them. There is no telemetry sample timestamp on this contract.

## Watch out
Identification QoS is explicitly supported by server tests. Preserve the fixed local pipe, token/session authority, 64 KiB signed framing, exact response ID/version, strict JSON, nullable numeric readings and bounded cancellation. A cached DeviceVerified value is a service claim, not independent host/HID identity. The std/JSON/OS runtime dependency closure belongs only to the developer CLI, never the four shared libraries.
