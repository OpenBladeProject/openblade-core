---
title: Native setRefreshRate parity and the shared cargo cache trap
author: Jack
date: 2026-11-19
branch: feat/native-agent-manual-brightness
cost: { minutes: 30, tokens: 54633 }
touches:
  - captures/
  - rust/crates/openblade-platform-windows/src/display/fence.rs
  - rust/crates/openblade-platform-windows/src/display/hdr.rs
  - rust/crates/openblade-platform-windows/src/display/native.rs
  - rust/crates/openblade-platform-windows/src/display/refresh_packet.rs
  - rust/crates/openblade-platform-windows/src/display/refresh_write.rs
  - rust/crates/openblade-platform-windows/src/display/refresh_write/tests.rs
  - rust/crates/openblade-platform-windows/src/display/tests.rs
  - tools/openblade-windows-host/src/session_native/display_query.rs
  - tools/openblade-windows-host/src/session_native/display_query/hdr_write_tests.rs
---

## Decided
- Native refresh writes mirror managed WindowsDisplaySettingsController: VALIDATE, then fence topology/admit, then exactly one APPLY (0x28060 / 0x282a0), then both desktop and active-signal readback. Every failure after APPLY is uncertain; nothing is replayed.
- EDID-derived rationals reduce only when a term exceeds u32 (managed DisplayConfigRational.FromRatio). Always reducing changed the issue #884 packet away from the physically validated encoding.
- The requested key must reach option building (managed AddDynamicRefreshOptions), or non-default Dynamic options are refused.
- The native refresh gate reopens on any available read that observed refresh state, classified or not. The managed pause still needs a complete readback. Gating on a classified key trapped repair of an unclassified mode (60 Hz desktop over 240 Hz signal) across restarts.
- HDR and refresh share display_query/ccd_write.rs; brightness keeps its own frame because managed differs there (no-op skips EnsureCurrent, fatal attempt errors).
## Tried and ruled out
- Treating a passing shared-crate test count as evidence without a Compiling line: binaries in C:\build-cache\cargo\openblade-core are reused across checkouts when sources are older than the last build, so the count came from another checkout's code.
## Watch out
- Before trusting a shared-crate (rust/crates/openblade-platform-windows) test count, touch its src/lib.rs and confirm "Compiling openblade-platform-windows (<this checkout>)". Host agent builds are unaffected.
- Nothing here is physically validated; real SetDisplayConfig acceptance of the explicit #884 timing is still open.
