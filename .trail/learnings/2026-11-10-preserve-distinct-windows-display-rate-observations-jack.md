---
title: Preserve distinct Windows display rate observations
author: Jack
date: 2026-11-10
branch: feat/rust-windows-display-topology
touches:
  - rust/crates/openblade-platform-windows/src/display.rs
  - src/OpenBlade.Display/WindowsDisplaySettingsController.cs
---

## Decided
Read the current user's active DisplayConfig topology with virtual-mode and virtual-refresh awareness. Keep the path refresh rational, matching target timing rational and DRR boost bit as separate observations. Resolve source and target identity only when exactly one active internal connector matches the managed selector.

## Tried and ruled out
The managed classifier tests cover a fixed desktop rate with a higher physical signal and require the boost bit to classify DRR. Neither a rate difference nor the preferred timing identifies the current DRR mode or substitutes for missing timing.

## Watch out
CCD arrays belong to a single successful query. Retry insufficient buffers with fresh sizing, validate returned lengths and the selected target mode's type, LUID and ID, and keep native status separate from empty success. The adapter is a leaf with no Session Agent caller. Its Windows tests link the native entry point and only call scripted APIs.
