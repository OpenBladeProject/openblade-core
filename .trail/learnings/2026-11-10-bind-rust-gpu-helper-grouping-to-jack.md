---
title: Bind Rust GPU helper grouping to read-only parent snapshots
author: Jack
date: 2026-11-10
branch: feat/rust-gpu-owning-app
touches:
  - rust/crates/openblade-platform-windows/Cargo.toml
  - rust/crates/openblade-platform-windows/src/gpu_apps/sampling.rs
  - rust/crates/openblade-platform-windows/src/gpu_apps/sampling/native.rs
  - rust/crates/openblade-platform-windows/src/gpu_apps/sampling/scripted.rs
  - rust/crates/openblade-platform-windows/src/gpu_apps/sampling/tests.rs
  - tests/OpenBlade.Platform.Tests/WindowsGpuActivityTests.cs
  - tests/OpenBlade.Tray.Tests/RustSessionAgentTests.Gpu.cs
---

## Decided
Feed a read-only Toolhelp PID-to-parent snapshot into the existing Rust GPU projection for each populated frame. Inspect candidates and their ancestors with the existing query-limited identity reader once per PID per frame. Preserve same-name, session, current-user, protection and per-edge creation-time checks.

## Tried and ruled out
A GPU engine counter only identifies its own PID. Requiring a parent to have a counter drops valid owning applications, while accepting a bare parent PID loses identity evidence. Trail check's lexical collisions on the new assertions describe unrelated installer, UI, display and Session Agent lifecycle work.

## Watch out
Missing or partial parent observations leave an otherwise stable helper visible. The private Rust path still requires corroborating parent links and identities across both frames and keeps foreign-account children separate. Managed CreateApps uses a single post-sampling process table. Scripted counters and the private v2 C# pipe verify projection and IPC. Live Toolhelp, PDH and DXGI behavior remain untested.
