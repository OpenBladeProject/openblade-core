---
title: Review exact-base Trail matches for the Windows SnapTap adapter
author: Jack
date: 2026-11-10
branch: feat/rust-windows-snaptap-adapter
touches:
  - rust/crates/openblade-platform-windows/src/snap_tap/adapter.rs
  - rust/crates/openblade-platform-windows/src/snap_tap/adapter/native.rs
---

## Decided
Check this slice against the pinned local policy parent. The separate adapter carries the Win32 calls; policy-only replay remains usable with a recording sink.

## Tried and ruled out
Trail matched vocabulary shared by unrelated firmware, UI, and driver work. The findings do not describe reintroduced behavior. The adapter keeps the confirmed-output and retirement proofs separate.

## Watch out
Failed unhook and timer retirement stay visible, and no replacement helper may use that report as a clean handoff. The change does not switch product backends.
