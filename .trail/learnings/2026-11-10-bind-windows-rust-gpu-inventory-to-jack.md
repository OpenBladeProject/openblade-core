---
title: Bind Windows Rust GPU inventory to sampled identities
author: Jack
date: 2026-11-10
branch: feat/rust-phase-6n-gpu-sampling
touches:
  - rust/crates/openblade-platform-windows/src/gpu_apps.rs
  - rust/crates/openblade-platform-windows/src/gpu_apps/sampling.rs
  - rust/crates/openblade-platform-windows/src/gpu_apps/sampling/native.rs
---

## Decided
Keep Windows GPU inventory local to the Rust adapter and its existing projection. A fresh DXGI 1.1 adapter description establishes the NVIDIA LUID, and each available PDH sample checks that the adapter still matches. Collect identity through one query-limited handle per observed PID and reject creation times newer than the counter sample.

## Tried and ruled out
A PDH fill that returns MORE_DATA or INVALID_ARGUMENT gets one fresh zero-size probe. Microsoft documents that a short fill can return an unreliable size. The managed parent table requires enumerating processes, so the native sampler supplies no parent provenance.

## Watch out
An available empty frame differs from missing PDH data, malformed array bounds, and failed calls. Reset the prime and stable pair after unavailable frames. The local link and scripted tests do not establish live PDH behavior, DXGI power behavior, or Session IPC parity.
