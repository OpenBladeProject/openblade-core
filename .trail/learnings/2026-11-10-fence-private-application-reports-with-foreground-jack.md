---
title: Fence private application reports with foreground watcher authority
author: Jack
date: 2026-11-10
branch: feat/rust-windows-phase-6w
touches:
  - rust/crates/openblade-application-reporter/src/lib.rs
  - rust/crates/openblade-platform-windows/Cargo.toml
  - rust/crates/openblade-platform-windows/src/application.rs
  - rust/crates/openblade-platform-windows/src/application/native.rs
  - tools/openblade-windows-host/Cargo.lock
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/src/windows/reporter_tests.rs
  - rust/crates/openblade-platform-windows/src/application/watcher.rs
  - rust/crates/openblade-platform-windows/src/application/watcher/native.rs
  - rust/crates/openblade-platform-windows/src/application/watcher/replay.rs
---

## Decided

Use one opt-in registration thread and a single latest notification slot. Resolve on the owner, then compare the notification revision before handing evidence to the reporter. A desktop boundary survives coalescing. Check the current foreground before emission even when no callback has arrived. Keep exact retained handles for liveness and release local evidence on session loss without renewing the service lease.

## Tried and ruled out

A window token alone cannot order two callbacks with the same HWND. A cancellation timeout requests worker shutdown without joining inside the startup wait. An inactive session cannot send a clear, so keep the prior sequence and reconcile on fresh authority.

## Watch out

The scripted runner covers notification ordering, authority loss, handle retirement and typed private v79 reporting without starting WinEvent. The existing pipe owns its own peer admission. Hook delivery, native startup and teardown, and session transitions still need separately authorized current-user validation. Keep the watcher out of shipping defaults.
