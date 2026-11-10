---
title: Keep independent write fences in the shared Rust Session Agent
author: Jack
date: 2026-11-10
branch: feat/rust-windows-phase-6y
touches:
  - tools/openblade-windows-host/src/session_windows/integrated.rs
  - tests/OpenBlade.Tray.Tests/RustSessionAgentTests.Integrated.cs
---

## Decided
Compose the pinned Display restart and reporter changes at a two-parent checkpoint. Run both claimed private Session Agent endpoints and the scripted v79 reporter on one current-thread runtime with separate dispatch futures. Require the exact service child PID and same Windows session before sending a frame. The private service independently admits the client's logon token before returning status.

## Tried and ruled out
Separate fixture processes could not demonstrate that a GPU timeout and reporter activity preserve both pending-write domains in one Session Agent lifecycle. The combined managed test uses existing adapters and deliberately refuses startup when the GPU claim is held or the service PID is wrong.

## Watch out
Use a fresh reporter instance per launch and keep the Display claim through process exit when an abandoned worker may still own an attempt. The explicit reconciliation launch retires only a verified Display record after readback. The offline result does not establish native hook, display, inventory or production cutover behavior.
