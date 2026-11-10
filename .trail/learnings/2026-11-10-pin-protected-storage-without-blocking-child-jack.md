---
title: Pin protected storage without blocking child replacement
author: Jack
date: 2026-11-10
branch: feat/rust-protected-storage
touches:
  - tools/openblade-windows-host/src/configuration_writer/protected.rs
  - tools/openblade-windows-host/src/configuration_writer.rs
---

## Decided
Use read/write sharing and withhold delete sharing on retained directory handles. Record handles block data writers and replacement during inspection and are dropped before a future writer can use the root. Check effective ancestor ACEs for path substitution authority, including DELETE_CHILD, independently of the root and record ACL rules.

## Tried and ruled out
Read-only sharing on a retained root conflicts with the existing MoveFileExW writer. An exact leaf DACL does not establish which directory occupied an ancestor path before pinning.

## Watch out
Allow an unprivileged ancestor grant for creating a subdirectory when it conveys no existing-child replacement rights. Independently test the intended NTFS ancestor and root ACLs under the intended identity. Host composition still needs exclusive privileged-writer ownership.
