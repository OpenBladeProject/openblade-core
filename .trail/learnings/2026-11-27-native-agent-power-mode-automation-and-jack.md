---
title: Native Agent power-mode automation and session power relay
author: Jack
date: 2026-11-27
branch: feat/native-agent-power-automation
touches:
  - ".github/workflows/rust.yml"
  - rust/README.md
  - rust/crates/openblade-domain/src/profile_set/decode.rs
  - rust/crates/openblade-platform-windows/src/agent_diagnostics.rs
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - tests/OpenBlade.Deployment.Tests/WindowsNativeEpochTests.cs
  - tests/OpenBlade.Session.Tests/OpenBlade.Session.Tests.csproj
  - tests/OpenBlade.SessionAgent.Tests/OpenBlade.SessionAgent.Tests.csproj
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/build.rs
---

## Decided
- Native power-mode automation (`agent-power-mode-automation-v1`, session_native/power_mode) and the session power relay client (`agent-session-power-relay-v1`, session_native/power_relay) are separate features, each refused by feature and marker in both package readers.
- Managed never restores a Windows power mode; "restoration" on exit, profile change or unknown status is cancel-and-forget. Native matches. Do not add a revert.
- The relay shares `ReportChannel` with the application reporter through `exchange_on(payload, reply_budget, ...)`; the reporter keeps its reconcile-sized reply budget, the relay passes FRAME_TIMEOUT and its own 1.5 s budget bounds the whole exchange. `prime()` acquires the generation at role start so the first suspend does not spend its budget hashing the package.
- The relay request id is generated at send time, not in the Windows callback, so the callback does no fallible work.
- `transport_thread::host(name, work)` is the shared dedicated-thread runtime for transports that hold a `!Send` Service generation.

## Tried and ruled out
- An `Observed` enum with managed variant names (`AlreadyObserved`) trips clippy enum_variant_names; the vector replay compares Debug names with managed, so rename the enum, not the variants.
- A `finished` flag on the active relay slot is redundant: `finish` replaces the slot under the same lock.

## Watch out
- Parity vectors: `tests/OpenBlade.Session.Tests/WindowsPowerModeAutomationNativeVectorTests.cs` and `tests/OpenBlade.SessionAgent.Tests/SessionPowerTransitionRelayNativeVectorTests.cs` write `tools/openblade-windows-host/tests/data/*-vectors.json` when OPENBLADE_REGENERATE_NATIVE_POWER_MODE_VECTORS / OPENBLADE_REGENERATE_NATIVE_SESSION_POWER_RELAY_VECTORS name that directory; otherwise they verify.
- No native Service serves reportSessionPowerTransition yet (service_status/unserved.rs); the Service side lives on feat/native-lifecycle-ops. Packaging must stay refused until both are paired.
- Relay unit tests must not wait on real time: inject the QPC clock, and use a stepping clock (each read a full budget later) to exercise callback expiry.
