---
title: Gate the Windows adapter query on an opened handle and owner handoff
author: Jack
date: 2026-11-09
branch: feat/rust-windows-adapter-power
touches:
  - docs/windows-backend-replay.md
  - tools/openblade-windows-host/src/discover.rs
  - tools/openblade-windows-host/src/discovery/mod.rs
  - tools/openblade-windows-host/tests/standard_transport.rs
  - tools/openblade-windows-host/tests/standard_transport/io.rs
  - tools/openblade-windows-host/tests/standard_transport/replay.rs
  - tools/openblade-windows-host/tests/standard_transport/session.rs
  - tools/openblade-windows-host/tests/standard_transport/windows.rs
  - tools/openblade-windows-host/src/adapter_power/io.rs
  - tools/openblade-windows-host/src/adapter_power/mod.rs
---

## Decided
Phase 3c uses a fixture-only RZ09-0581 07/8C adapter. Reuse the Phase 3a dual-source registry comparison, require one opened 1532:02E0 collection with usage 0001:0002 and 91-byte feature reports, and repeat host and strict authority checks after the candidate scan. Preserve report ID zero, 30 ms pacing, and a fresh managed-style transaction context.

## Tried and ruled out
SetupAPI hints, a shareable HID handle and the managed process-local feature gate cannot authorize a separate Rust device owner. Keep the Win32 evidence and transport compiled only into the integration target until an actual coordinated lease exists.

## Watch out
The typed GET sends a SET feature request. A later attended run needs one explicitly authorized exchange, exclusive control and matrix handoff, terminal drain before handback, and independent managed reconciliation after uncertainty.
