---
title: Guard Rust scaffold policies and caller independent checks
author: Jack
date: 2026-11-08
branch: build/rust-workspace-ci
touches:
  - rust/Check-Workspace.ps1
  - rust/Test-WorkspaceCheck.ps1
  - ".github/workflows/rust.yml"
  - rust/README.md
---

## Decided
Keep PR2's compiler-safety and inward-dependency restrictions until the separately reviewed implementation stages change them. Check each resolved crate MSRV against the active exact toolchain and verify retained workspace forbid-unsafe/lint inheritance/no_std policies. Resolve Check-Workspace.ps1 from PSScriptRoot and restore caller location. Let rustup read its own toolchain TOML rather than parse channel formatting in workflow YAML.

## Tried and ruled out
The original exact-head checker accepted lowered MSRV, relaxed unsafe lint, removed lint inheritance and removed no_std in offline workspace copies. Guard regression tests now reject those mutations and verify repository-root invocation and equivalent TOML spelling. Claude's future ABI/test-target/doc-test concerns do not establish a current phase-2 defect. PR1's first-stage document is historical; do not rewrite it as if PR2 exists at PR1 alone.

## Watch out
Claude advisory review was complete for 652334a1...d057433e, not a later dependency refresh. Preserve exact reviewed scope and raw run artifacts locally; validate findings rather than treating every future-proofing suggestion as an authorization to start PR3. Trail check can flag lexical overlaps in build/policy prose; review and explain those without weakening production rules.
