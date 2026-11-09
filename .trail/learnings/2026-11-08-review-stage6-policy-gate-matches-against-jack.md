---
title: Review stage6 policy gate matches against the actual dormant scope
author: Jack
date: 2026-11-08
branch: feat/rust-profile-policy
touches:
  - ".github/workflows/rust.yml"
  - docs/rust-boundaries.md
  - rust/Cargo.lock
  - rust/Check-Workspace.ps1
  - rust/README.md
  - rust/Test-WorkspaceCheck.ps1
  - rust/crates/openblade-domain/Cargo.toml
  - rust/crates/openblade-domain/src/lib.rs
  - rust/crates/openblade-ffi/include/openblade.h
  - rust/crates/openblade-ffi/src/lib.rs
---

## Decided
The stage6 Trail gate must be evaluated against exact predecessor f542888e205b8e41f10b7ac3193f637600affd74. Default-main checks include inherited stage3-5 changes. The checker remains unchanged. Its 20 initial stage6 hits were lexical overlaps in the boundary documentation: exact independent ABI equality is preserved, enum validation applies only to wire shape/legacy synthesis, contradictory fans remain unchanged, and synthetic fixtures do not claim hardware evidence.

## Tried and ruled out
Do not rewrite historical learnings or weaken the checker to make this gate green. Earlier stage exclusions describe those stages and do not prohibit explicitly authorized dormant stage6 profile code. No product activation, firmware writes, generated expectations, copied physical evidence or changed IPC/schema is introduced here.

## Watch out
Report the final nonzero lexical gate honestly in the commit and PR, and inspect the complete staged scope including new files. Source-backed merge tests, preservation sentinels, real native interop and exact-head hosted lanes are the behavioral evidence. Mandatory isolated Claude advisory review remains a separate gate with original reviewed and tested final heads distinguished.
