---
title: Keep Rust scaffolding native CI independent of Windows production
author: Jack
date: 2026-11-08
branch: build/rust-workspace-ci
touches:
  - rust/Cargo.toml
  - rust/Check-Workspace.ps1
  - rust/README.md
  - ".github/workflows/rust.yml"
---

## Decided
Phase 2 adds four empty, unpublished Rust libraries with Rust 1.99.1 pinned, edition 2024, MSRV equal to the pin and GPL-3.0-or-later. Keep the C# Windows product independent: no Rust build step, native library payload or runtime handoff. Native CI covers Windows x64, Linux x64, and both macOS architectures; pull_request has no base-branch filter so a draft stacked on PR1 actually runs. Preserve the existing conservative .NET classifier fallback for Rust and workflow inputs.

## Tried and ruled out
No duplicate parity fixtures or constant-only placeholder tests: the zero-test crates establish toolchain portability only. An ignored negative workspace with an added protocol-to-domain edge was rejected by the metadata boundary check.

## Watch out
Run trail check against the pinned stacked base as well as the default main comparison. Its lexical matches here concern workflow vocabulary, synthetic parity, unrelated hardware/UI learnings and earlier PR1 design prose; inspection found no conflicting production behavior. Explain the nonzero lexical result in the commit/PR, without weakening those learnings or calling the check green. Future implementations need separate behavior, ABI and hardware evidence. Hosted native jobs, rather than YAML parsing or cross-compilation, establish platform execution.
