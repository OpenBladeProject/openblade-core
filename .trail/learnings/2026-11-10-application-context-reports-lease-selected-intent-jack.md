---
title: application context reports lease selected intent
author: Jack
date: 2026-11-10
branch: feat/rust-phase-5m-application-profiles
touches:
  - docs/windows-rust-development-service.md
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.cs
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/src/main.rs
  - tools/openblade-windows-host/src/windows.rs
  - tools/openblade-windows-host/src/wire.rs
  - tools/openblade-windows-host/src/application_runtime.rs
---

## Decided
The managed service gives the current application reporter instance a monotonic sequence watermark. A different admitted instance can start at one. Only a matched enabled rule has a 15-second lease. Explicit null, an unmatched canonical path, rule invalidation or expiry restores current power-profile selection without forgetting the sequence. Restart discards both.

## Tried and ruled out
A basename match, a normalized alias received on the wire, and guessing process lifetime from a reported path are not equivalent to the managed contract. The Session Agent owns running-process observation. The private Windows replay keeps native peer identity separate from its scripted active-session authority and rechecks authority at state admission.

## Watch out
The v79 nullable executablePath is required; absence is not an explicit reset. A stale or refused report cannot renew a lease, advance a sequence, or publish a revision. Keep this development replay separate from durable profile writes and hardware effects; preserve manual override precedence and never infer confirmation from selected intent.
