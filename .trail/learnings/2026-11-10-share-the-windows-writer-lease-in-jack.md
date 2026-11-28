---
title: Share the Windows writer lease in both scratch generations
author: Jack
date: 2026-11-10
branch: feat/windows-writer-fence
touches:
  - tests/OpenBlade.WriterFence.Scratch/Program.cs
  - tests/OpenBlade.WriterFence.Scratch/WriterLease.cs
  - tools/openblade-windows-host/src/configuration_writer/scratch.rs
---

## Decided
Use configuration.writer-lock as the common Windows exclusive file open for the two scratch writers. Admit a local NTFS directory and empty single-link lease by their enrolled Windows IDs and retain the directory chain through disposal. Use an existing lease only. The coordinator creates and removes its private artifacts relative to admitted handles.

## Tried and ruled out
UserConfigurationRepository serializes migration within one repository instance, and the earlier managed owner handoff uses an in-process fence and a hardware-specific journal. Neither excludes a Rust process. Reacquiring the file after abrupt exit grants only exclusion. The scratch gate still requires independently observed exit, an exact-generation receipt and clear recovery evidence.

## Watch out
Trail check reports term overlap with lessons on readonly directory sharing, a Rust-only lease, production Setup rollback and uncommitted receipts. Directory handles allow read and write sharing. Both scratch children hold the same file, and the harness uses the existing scratch retirement gate with a retained child process and simulated SCM. The live protocol and opaque recovery bytes provide scratch evidence only. Production still needs writer-wide scope, protected ACL/token admission and durable boot/build/process-bound retirement evidence.
