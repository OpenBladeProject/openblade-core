---
title: Compile the installed Agent without development fixture modules
author: Jack
date: 2026-11-10
branch: feat/rust-installed-agent-candidate
touches:
  - docs/windows-rust-development-service.md
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/src/lib.rs
  - tools/openblade-windows-host/src/pipe.rs
  - tools/openblade-windows-host/src/session_agent.rs
  - tools/openblade-windows-host/src/session_native.rs
  - tools/openblade-windows-host/src/session_native/tests.rs
  - tools/openblade-windows-host/src/session_windows.rs
  - tools/openblade-windows-host/src/installed_agent_main.rs
  - tools/openblade-windows-host/src/session_native/token.rs
---

## Decided
Give the installed Session Agent candidate its own opt-in, no-argument entry point. Reuse the Windows token queries while keeping the development Session Agent fixture router, private pipes and scripted runtime outside the candidate binary.

## Tried and ruled out
Enabling a no-argument branch in the development executable still compiles the fixture modules into that role. Package-wide Cargo dependencies also do not describe a particular binary's module closure. Check rustc dep-info for each actual candidate target.

## Watch out
These are compile-only candidates. The Agent must fail closed without independently authorized sign-in and a managed endpoint lease, and the Service retains its independent SCM admission and readiness gates. Neither belongs in production Publish or Setup.
