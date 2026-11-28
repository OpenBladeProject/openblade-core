---
title: "Native lifecycle ops: shutdown via own STOP, reply-only power relay"
author: Jack
date: 2026-11-27
branch: feat/native-lifecycle-ops
touches:
  - ".github/workflows/rust.yml"
  - docs/windows-rust-development-service.md
  - rust/crates/openblade-domain/src/low_power_gate.rs
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - tests/OpenBlade.Deployment.Tests/WindowsNativeEpochTests.cs
  - tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj
  - tests/OpenBlade.Service.Tests/ServiceSessionPowerTransitionCoordinatorTests.NativeLifecycleVectors.cs
  - tests/OpenBlade.Service.Tests/ServiceSessionPowerTransitionCoordinatorTests.cs
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/build.rs
---

## Decided
- `shutdownOpenBlade` (service-lifecycle-shutdown-v1) waits on the thermal runtime gate (`Runtime::quiesced`), then asks for the Service's own STOP through `scm_host::deliver_stop`, which runs SCM's STOP ingress (revoke) and `Controls::handle`. No cleanup of its own; the existing drain owns it. The Logon::Other admission (elevated service accounts) is scoped to this op only; a test pins that other ops still close.
- `reportSessionPowerTransition` (service-session-power-relay-v1) is reply-only. It reads the gate through `Lifecycle::gate_view` (packed AtomicU64, no lock the SCM callback takes) and never applies a report. Its newer-transition check compares with the newer of the gate's view and its own last accepted stamp, which is what managed's gate would hold.
- An accepted report gets managed's deadline literal at once; managed's Applied texts are recorded in the vectors and asserted as a deviation.

## Tried and ruled out
- Byte-for-byte replay of managed frames: managed's member order differs from native's (`serde_json::to_value` sorts keys). Compare parsed Values, as the other parity tests do.
- Fixed sleeps (1 ms, then 20 ms) before counting the spawned zero-delay stop: flaky under full-suite load (0 vs 1). Use the bounded `stops_reaching` wait for positive counts.

## Watch out
- `trail check` on a branch cut from an older commit compares with origin main and reports hundreds of unrelated files; pass `--base <branch point>`.
- `tests/OpenBlade.Deployment.Tests` has its own restore. Without `obj/project.assets.json`, `dotnet test --no-restore` printed nothing and exited 0; check for a "Passed!" line, not the exit code.
