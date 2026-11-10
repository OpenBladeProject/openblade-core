---
title: Require the managed Session Agent ownership contract at native sign-in
author: Jack
date: 2026-11-10
branch: feat/rust-phase-9c-agent-launch
touches:
  - tools/openblade-windows-host/src/session_native.rs
  - tools/openblade-windows-host/src/session_windows.rs
  - src/OpenBlade.SessionAgent/Program.cs
---

## Decided

The fixed Session Agent task runs OpenBlade.SessionAgent.exe with its install directory as the working directory and no arguments. Its least-privilege Users group principal does not name a single authorized account. Gate the development Rust no-argument route on the installed action and an independently authorized interactive SID, process and token session, and authentication LUID. See src/OpenBlade.Setup/SignInTaskScheduler.cs and src/OpenBlade.SessionAgent/Program.cs.

## Tried and ruled out

A private fixture pipe or unrelated lock cannot prove production ownership. Managed startup takes first-instance leases for the SID/session display, GPU, audio, and battery endpoints. The event pipe is an outbound client role and the shutdown event is separate and keyed by executable path.

## Watch out

The native entry feature is compile-only. The real route reads process evidence and fails closed without trusted installed sign-in and production bindings. Synthetic lease, readiness and stop tests establish control flow. Keep the default binary and explicit fixture modes separate from production admission.
