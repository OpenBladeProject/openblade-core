---
title: Separate root and battery record admission on Windows
author: Jack
date: 2026-11-10
branch: feat/rust-protected-storage
touches:
  - tools/openblade-windows-host/src/configuration_writer/protected.rs
  - src/OpenBlade.Setup/MachineDataDirectorySecurity.cs
---

## Decided
Phase 4h pins a local root and admits the battery aggregate on retained read-only handles. The root must match #912’s applied ACL. Each applicable existing record needs its own trusted owner and DACL with no untrusted mutation grant. A trusted recovery-required record fences by presence.

## Tried and ruled out
Do not infer child trust from the root ACL. Normal service setup deliberately skips descendant adoption. The developer fixture keeps its existing root and behavior.

## Watch out
Before composing installed startup, prove the positive ACL path using setup-equivalent protection and the intended identity on an isolated NTFS runner, and establish exclusive privileged-writer ownership and a coherent snapshot. Leave existing transaction artifacts and specialty journals to their own recovery paths.
