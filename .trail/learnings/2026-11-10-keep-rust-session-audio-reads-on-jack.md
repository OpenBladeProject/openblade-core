---
title: Keep Rust Session Audio reads on the private v2 fixture
author: Jack
date: 2026-11-10
touches:
  - rust/README.md
  - tests/OpenBlade.Tray.Tests/RustSessionAgentTests.cs
  - tools/openblade-windows-host/src/session_agent.rs
  - tools/openblade-windows-host/src/session_windows.rs
  - tests/OpenBlade.Tray.Tests/RustSessionAgentTests.Audio.cs
  - tools/openblade-windows-host/src/session_audio.rs
---

## Decided

The managed Session Audio pin at 6d6abc9ba28f88c102e295567f73cf11e3bbfabe uses v2. The Rust development host can exercise an explicit Read of either endpoint on its separate private pipe with synthetic state and no base-audio proof.

## Tried and ruled out

The pinned managed codec can act as the interoperability oracle without the tray's production adapter or an audio worker. The private connection closes before dispatch on Discover, Connect, mutations and unknown operations.

## Watch out

A typed Unavailable reply differs from an expired dispatch, which closes the connection without a reply. Fixture cancellation can drop the synthetic future immediately. Native parity still needs cooperative cancellation or worker settlement under supervision, independent admission and confirmed-state recovery.
