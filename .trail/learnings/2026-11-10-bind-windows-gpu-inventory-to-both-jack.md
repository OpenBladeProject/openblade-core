---
title: Bind Windows GPU inventory to both process observations
author: Jack
date: 2026-11-10
branch: feat/rust-windows-gpu-apps
touches:
  - rust/README.md
  - rust/crates/openblade-platform-windows/src/lib.rs
  - rust/crates/openblade-platform-windows/src/gpu_apps.rs
  - rust/crates/openblade-platform-windows/src/gpu_apps/tests.rs
---

## Decided

The Rust Windows Session GPU inventory projects scripted PDH counters and identity observations. Keep available empty separate from unavailable, and keep zero-percent engine holders in the two-sample intersection. Only an identity tied to both samples may become an app row. Preserve the managed sort, ambient omissions, refusal reasons and 128-row query cap.

## Tried and ruled out

A stable PID alone can carry activity across process reuse. A later lookup cannot establish who owned the earlier sample. Missing evidence stays activity-only and conflicting identities stay stale. Parent attribution also requires a stable edge and same-user ownership.

## Watch out

CanClose describes the inventory row. A native reader still needs coherent process metadata tied to PDH collection and cancelable sampling. A future close path needs fresh scan matching and retained-handle identity checks. This layer has no action or IPC transport.
