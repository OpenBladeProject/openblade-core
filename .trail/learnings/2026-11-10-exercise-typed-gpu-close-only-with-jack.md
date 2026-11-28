---
title: Exercise typed GPU close only with fixed Rust fixture records
author: Jack
date: 2026-11-10
branch: feat/rust-gpu-close-fixture
touches:
  - rust/crates/openblade-platform-windows/src/gpu_apps.rs
  - rust/crates/openblade-platform-windows/src/gpu_apps/sampling.rs
  - rust/crates/openblade-platform-windows/src/gpu_apps/sampling/scripted.rs
  - tests/OpenBlade.Tray.Tests/RustSessionAgentTests.Gpu.cs
  - tools/openblade-windows-host/src/session_gpu.rs
  - tools/openblade-windows-host/src/session_windows.rs
  - rust/crates/openblade-platform-windows/src/gpu_apps/action.rs
  - rust/crates/openblade-platform-windows/src/gpu_apps/action/fixture.rs
  - rust/crates/openblade-platform-windows/src/gpu_apps/action/tests.rs
  - target-local/.rustc_info.json
---

## Decided
The dedicated Windows gpu-foreground simulated-close fixture exercises typed v2 replies against fixed in-memory records. Preserve real pipe peer checks, fresh two-sample attribution, retained PID, creation time, SID, session, executable, owning app and protected-process checks. Match from the complete fresh projection and cap the serialized inventory separately. Query rows still deny close authority, and existing GPU fixture modes reject close before sampling.

## Tried and ruled out
Do not derive authority from presentation or transfer confirmation from a helper to an owning PID. Keep managed dispatcher ordering and success/refusal strings, but do not copy the managed tree-to-exact force fallback. A possibly submitted action ends the simulated batch; later targets are reported as unattempted.

## Watch out
The fixed records are fixture evidence only. Real retained process ownership and executable provenance, user confirmation, cancellation after submission, read-only reconciliation and preventing replay across requests need separate design and authorization. The integrated fixture does not accept simulated-close.
