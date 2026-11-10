---
title: Bind foreground identity to a retained Windows process handle
author: Jack
date: 2026-11-10
branch: feat/rust-windows-phase-6q
touches:
  - rust/crates/openblade-platform-windows/src/application.rs
  - rust/crates/openblade-platform-windows/src/application/native.rs
  - rust/crates/openblade-platform-windows/src/application/path.rs
  - rust/crates/openblade-application-reporter/src/lib.rs
---

## Decided
Bind the observed HWND to the active local console and Default desktop on both sides of image lookup. Keep one query/synchronize process handle for the sampled executable and its lifetime. Coalesce pending foreground samples without reopening by PID.

## Tried and ruled out
System ICU alone misses supplementary Garay casing; the newer Rust Unicode table would also admit Beria Erfe matches outside the pinned managed contract. Use system ICU for BMP and the .NET 10 supplementary uppercase ranges. Decline ordinary tilde paths rather than infer filesystem-dependent short-name expansion.

## Watch out
This binding starts no watcher or IPC. A future host must verify observation ordering and teardown, prompt background exit delivery, and current-user IPC/reconnect/cancellation before production use. Recheck the path and ordinal contract on runtime or ICU changes.
