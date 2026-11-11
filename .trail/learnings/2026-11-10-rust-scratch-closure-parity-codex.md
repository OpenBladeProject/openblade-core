---
title: Match scratch closure digests and retain sealed namespaces in Rust
author: Codex
date: 2026-11-10
branch: fix/rust-scratch-closure-verification
touches:
  - tools/openblade-windows-host/src/candidate_receipt.rs
  - tools/openblade-windows-host/src/scratch_receiver_closure.rs
  - tools/openblade-windows-host/src/scratch_receipt_policy.rs
  - tools/openblade-windows-host/tests/fixtures/candidate_receipt.rs
  - tools/openblade-windows-host/tests/candidate_receipt_offline.rs
  - tests/OpenBlade.Setup.Tests/ScratchReceiverClosureParityOfflineTests.cs
---

The launch evidence namespace is candidate-setup-launch.v2; the canonical closure
encoding still uses its v1 prefix. Check the filename against the real C#
EvidenceName as well as testing the digest. Rust and its fixture once agreed on
the obsolete .v1 filename, masking failure to open C# evidence.

Scratch launch evidence bytes 56..88 identify the complete app-local receiver
closure, not the apphost alone. Match the real C# BinaryWriter encoding: prefix,
ordinal member names with 7-bit UTF-8 byte lengths, little-endian Int64 sizes and
raw SHA256 member hashes. A shared data-only vector includes satellites, Windows
RID assemblies and a long name. Retain every checked file and namespace through
the second readiness decision, enumerate via retained handles, and revalidate
identity, ACL, size/hash and exact membership. Require the exact OWNER RIGHTS
seal only inside scratch receiver namespaces; keep installed DACL policy intact.
The separately build-pinned readiness filename is the sole enumeration exception.
The installed shared .NET framework remains outside this app-local proof.

Authenticate valid earlier receipt, binding and launch evidence before testing a
later rejection; assert its concrete error to catch accidental early refusal.
Keep synthetic evidence inside test harnesses. Offline streams and NTFS files
prove reader guards and cleanup, not kernel pipe peers, live generation/lifetime
or installed/Burn authority. Compile the real scratch binary with its native
peer-verifier module and check actual combined all-target builds: test=false
means a speculative fixture test-harness failure is not compiler evidence.
