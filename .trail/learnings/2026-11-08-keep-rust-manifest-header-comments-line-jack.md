---
title: Keep Rust manifest header comments line bounded
author: Jack
date: 2026-11-08
branch: build/rust-workspace-ci
touches:
  - rust/Check-Workspace.ps1
  - rust/Test-WorkspaceCheck.ps1
---

## Decided
In the Rust scaffold guard's DOTALL section matcher, keep TOML header comments bounded to a single line with [^\r\n]*. Assignment comments use the same explicit bound. Positive disposable-workspace tests retain comments on the root workspace-lint section and every member lints section, including members followed by dependencies.

## Tried and ruled out
A header comment matched with #.* under (?ms) greedily consumed later lines and left an empty section body. Valid commented manifests were rejected even though plain headers and the initial negative policy cases passed.

## Watch out
Negative rejection tests do not establish acceptance of equivalent valid input. Keep the native four-host guard tests and let rustup interpret its own toolchain file; this parser is a narrow manifest policy check, not a general TOML implementation.
