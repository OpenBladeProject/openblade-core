---
title: Preserve Windows pipe ownership while retiring busy listeners
author: Jack
date: 2026-11-09
branch: feat/rust-windows-readonly-host
touches:
  - docs/windows-rust-development-service.md
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/src/main.rs
  - tools/openblade-windows-host/src/windows.rs
  - tools/openblade-windows-host/src/windows/pipe.rs
---

## Decided
The Windows fixture host retains a synchronous, self-paired first pipe instance for ownership while it replaces the listening instance. Promptly retire overflow and dropped clients, retry transient instance exhaustion under a deadline, and report stopped after retiring active tasks and releasing the claim.

## Tried and ruled out
Reusing a rejected Tokio listener risks carrying its prefetched read state into the next client. A Tokio-based ownership pair also held overlapped handles through the stopped notification in the native lifecycle test.

## Watch out
A completely full instance budget can return ERROR_PIPE_BUSY before Windows checks first-instance contention. Test claim preservation separately after releasing the ordinary listening instances.
