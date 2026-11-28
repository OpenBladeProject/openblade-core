---
title: Native stored replies follow managed; IPC79 and custody corrections
author: Jack
date: 2026-11-19
branch: fix/native-branch-red-tests
touches:
  - build/NativeWindowsFixtureManifest.json
  - rust/Check-Workspace.ps1
---

## Decided
- Native setPerformanceOverride and setFanOverride replies follow managed. Managed returns Stored without applying only for its manual-fan performance deferral (`ServiceControlRuntime.DeferredPerformanceIntentMessage`, used through `deferHardwareApply`). Every other saved but unapplied override is Blocked, with the message "<committed> Hardware result: <cause>", as `ExecuteScopedMutationAsync` builds it. Native thermal now returns Stored only for `Cause::ManualDeferred`. GPU incompleteness, transaction capacity, power change, no eligible policy, storage and transport causes are Blocked. Native fan has no Stored reply. This corrects the "capacity refusal stays Stored, as managed reports persisted-but-unapplied intent" decision recorded with 694f65ad.
- `NativeThermalMutationResponseTests.ActualNativeMutationResponsesUseManagedV80CodecAndOutcomes` pins this through the managed v80 codec. The thermal corpus in `build/NativeWindowsFixtureManifest.json` now includes the manual-deferral, capacity-refusal and power-change cases.
- The 13 `private_mutation` readiness cases are `#[ignore]`d. No runner prepares their roots, and the manifest has no case for them. The earlier "skip outside the fixture runner" rationale (fafb6b55) was wrong: those early returns passed without running anything.
- The dev host wire test is now `rejects_frozen_ipc79_requests_without_reply`, which covers the frozen `ipc79/service.json` requests. `ipc79/service-status-event.json` was unused and is deleted. The native Service status client and server (`service_status.rs`) are v80. Only `reporter_pipe.rs` is still v79.
- `ServiceMailboxHandoff` no longer touches the machine store during DI resolution. `ServiceMailboxCustodyStartup` is the first hosted service, so the host builds it after its lifetime starts and before Worker; it calls `Open()`. Until then the production `MailboxLifetime` refuses `Reserve()`.
- `rust/Check-Workspace.ps1` admits openblade-domain's three optional thermal-history roots (exact versions and features, plus the sha2 package closure). It rejects any workspace crate that enables the feature. It also admits the six windows-sys features from 27e6f725 and the two protocol tests from cbf8aacc and a882588c.

## Tried and ruled out
- Making the handoff lazy on its first `Lifetime` access. Every factory reads `Lifetime` while it is being built, so DI resolution would still do the I/O.
- Opening custody in `Program.cs` before `host.Run()`. That runs before the SCM dispatcher connects, inside the service start window.

## Watch out
- The candidate-receipt Setup variant reaches the codec's generic writer through `InternalsVisibleTo`. `IpcMessageCodec.WriteFramedAsync` must stay internal.
- Pair `#[cfg_attr(test, allow(dead_code))]` on `set_ingress`/`installed_ingress` with the module-level allows in `main.rs`, `lib.rs`, `installed_main.rs` and the candidate fixture. Removing either set raises the feature-lane clippy counts.
- To emulate the fixture runner without Python, run cargo with `--exact --nocapture --test-threads=1 --quiet`. Without `--quiet`, the first frame shares the libtest line and the runner treats it as unanchored.
