---
title: Replay the standard Windows HID feature exchange offline
author: Jack
date: 2026-11-09
branch: feat/rust-windows-transport
touches:
  - docs/windows-backend-replay.md
  - tools/openblade-windows-host/Cargo.lock
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/tests/standard_transport.rs
  - tools/openblade-windows-host/tests/standard_transport/io.rs
  - tools/openblade-windows-host/tests/standard_transport/replay.rs
  - tools/openblade-windows-host/tests/standard_transport/session.rs
  - tools/openblade-windows-host/tests/standard_transport/windows.rs
---

## Decided

Phase 3b replays the RZ09-0581 adapter-power query in a Cargo integration-test target. The standard managed route submits the 91-byte Windows frame to DeviceIoControl SET as input, waits 30 ms in the production battery path, and supplies a fresh output buffer for GET. The shared codec owns the 90-byte envelope.

## Tried and ruled out

An opened HID handle and a developer binary entry point would bypass the offline scope. The tests use sanitized payload evidence, scripted native outcomes, and new non-HID pipe handles for Win32 linkage and targeted cancellation.

## Watch out

CancelIoEx may race a successful completion or fail to find a request. Keep the handle and storage through terminal completion and retain both outcomes. After ambiguous submission, retire the session and require independent read-only reconciliation. Metadata discovery cannot replace the exact opened-handle and controller admission needed for a later authorized physical query.
