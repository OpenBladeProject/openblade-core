---
title: Bind the installed Rust candidate to exact SCM launch evidence
author: Jack
date: 2026-11-10
branch: feat/rust-installed-service-boundary
touches:
  - tools/openblade-windows-host/src/windows/scm/installed.rs
  - tools/openblade-windows-host/src/windows/scm/installed/native.rs
  - docs/windows-rust-development-service.md
---

## Decided
Use a dedicated Cargo binary and opt-in feature for the installed OpenBlade SCM role. Check exact Setup registration, callback, image, LocalSystem session and startup PID before candidate prerequisites or endpoint preparation.

## Tried and ruled out
The ordinary and VM pipes have private names, authorization and scripted state. Neither satisfies the installed v79 GetStatus readiness contract. The signed development package does not attest installed bytes or ownership.

## Watch out
SCM can omit the PID in START_PENDING. Fail closed pending VM evidence or another independent binding. Preserve a provider retirement error after startup cancellation. Validate the actual SCM boundary and installed typed readiness in a separately authorized disposable VM.
