---
title: Build both Windows Rust fixture roles from one integration checkpoint
author: Jack
date: 2026-11-10
branch: test/rust-windows-integrated-fixtures
touches:
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.PrivateRust.cs
---

## Decided

Keep Setup 7e as the first parent, then record separate local merges of exact 5r and 6y. Resolve the host manifest and module declarations by retaining the CPU-worker opt-in, guardian, Agent, reporter, and existing Windows replay features. Pin both signed scratch roles to the same integrated Rust merge commit and keep their hashes independent. Leave the managed IPC contract trees byte-identical.

## Tried and ruled out

The exact 5r and 6y overlap reproduced only in Cargo.toml and src/lib.rs. The existing scratch Setup rehearsal covered successful cutover and a typed GPU timeout with byte-exact rollback. The same service bytes passed the five managed v79 Fan Stop cases, so a new harness was unnecessary.

## Watch out

A default Trail comparison from the unpublished stack's old merge base includes inherited workflow and docs. Check the local integration delta separately. The scratch results cover scripted peers and process lifetime. Native registration, hardware handback, and installed rollout and rollback need separate validation.
