---
title: Retain protected writer authority through private pipe teardown
author: Jack
date: 2026-11-10
branch: feat/rust-protected-runtime
touches:
  - tools/openblade-windows-host/src/configuration_writer.rs
  - tools/openblade-windows-host/src/configuration_writer/protected.rs
  - tools/openblade-windows-host/src/windows.rs
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.Protected.cs
---

## Decided
The Windows developer host must retain the admitted root and an exclusive same-file writer lease through pipe teardown and the blocking transaction drain. Stage and replace within that retained directory, verify the preimage and readback by identity and bytes, and preserve the fixed recovery fence on uncertainty.

## Tried and ruled out
A Rust-only lease cannot exclude the installed managed writer. Its configuration semaphore is process-local. The private C# oracle cooperates by holding FileShare.None around the actual managed store, which limits the handback result to that fixture.

## Watch out
Positive admission requires an already-enabled non-SYSTEM Administrator on disposable NTFS with a trusted default owner. Verify actual managed-created file ownership without changing it. Ordinary replay and unprivileged rename tests do not establish positive protected admission. A same-repository PR also triggers the hosted SCM smoke.
