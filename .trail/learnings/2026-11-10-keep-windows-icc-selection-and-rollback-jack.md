---
title: Keep Windows ICC selection and rollback evidence separate
author: Jack
date: 2026-11-10
branch: feat/rust-windows-icc-sdr
touches:
  - rust/Check-Workspace.ps1
  - rust/crates/openblade-platform-windows/Cargo.toml
  - rust/crates/openblade-platform-windows/src/lib.rs
  - rust/crates/openblade-platform-windows/src/color_profile.rs
  - rust/crates/openblade-platform-windows/src/color_profile/native.rs
  - rust/crates/openblade-platform-windows/src/color_profile/tests.rs
  - rust/crates/openblade-platform-windows/src/color_profile/transaction.rs
---

## Decided
The isolated Rust Windows ICC reader uses the target adapter LUID with the source ID for WCS. It reads the selected scope and adds only the five managed vendor presets with valid monitor RGB headers. Only a confirmed SDR observation selects an SDR default.

## Tried and ruled out
The managed transaction can restore after a setter fails with a visible side effect. The scripted Rust transaction treats that effect as unowned and stops. It can restore previous verified steps when a fresh read shows that the failed call left the last known state intact.

## Watch out
Use Windows filename equivalence for profile selection. Retain the raw ID and compare full reported paths when checking baseline continuity. Recheck the display, monitor key, scope and independently supplied HDR state before scripted mutations and recovery. This phase provides no native write binding or product integration.
