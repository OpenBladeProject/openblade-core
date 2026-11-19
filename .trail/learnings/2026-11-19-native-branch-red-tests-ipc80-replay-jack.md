---
title: "Native branch red tests: IPC80 replay, composition expectations, capacity refusal"
author: Jack
date: 2026-11-19
branch: fix/native-branch-red-tests
---

## Decided
- The managed replay oracle (Contracts and Service `WindowsReplay/v1`) tracks the current IPC version and has no generator: bump it by hand with the protocol. The read-only dev host (`main.rs`, `wire.rs`) still speaks 79, so it embeds byte-identical copies from `tools/openblade-windows-host/tests/data/ipc79/` and must never include the managed files again.
- A known pre-publication capacity refusal (85de177f) reports Stored, because intent was persisted before the reservation and managed maps persisted-but-unapplied intent to Stored. Uncertain reserve I/O stays Blocked. Neither may reach Prepared, the submission intent, the journal or a write. The test pins both.
- Expectations that differ by Service composition use `cfg!(feature = ...)` in the test, not a narrower test. Three cases: the mailbox status text; `RUNNING_CONTROLS` (thermal adds power and session events); and two control-session reads per interactive getStatus under thermal, because the peer is refreshed for the private projection.
- Private fixture tests skip unless `OPENBLADE_NATIVE_EDGE_FIXTURES=1`. Managed tests use `NativeThermalEdgeFact`; Rust tests return early. Do not use `#[ignore]`: the fixture runner pins `--exact` without `--include-ignored`, and it always sets the switch.

## Tried and ruled out
- Treating the capacity-refusal failure as an ordering regression. At 9e5c08cc intent was already saved before the reservation; only quarantine changed.
- Rerunning ImagePin tests from a "trusted" directory to clear them. Every ancestor here, including the user profile Temp, grants Modify to sandbox SIDs. The pin is right to refuse; these tests are only green on a clean host.

## Watch out
- Lib clippy errors hide bin and test-target errors until the lib compiles. Iterate to EXIT 0, and also run the `rust/` workspace clippy.
- Fixing the v79/v80 assertion revealed a second, older failure (the readiness session-read count, from 725ddcf3) in the same test.
- ServiceCompositionTests fails on a host with protected `C:\ProgramData\OpenBlade`, because `ServiceMailboxHandoff` calls `RequireLegacyStoreAbsent`. That is environmental, but the test touches real machine state.
- Tray `RustReadOnlyHostTests` and the Service `Rust*Parity` CI steps send `OpenBladeIpc.ProtocolVersion` (80) to the v79 dev host.
