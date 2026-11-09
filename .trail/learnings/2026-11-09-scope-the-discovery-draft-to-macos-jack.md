---
title: Scope the discovery draft to macOS only
author: Jack
date: 2026-11-09
branch: feat/portable-hid-discovery
touches:
  - ".github/workflows/rust.yml"
  - rust/README.md
  - tools/openblade-discovery/README.md
  - tools/openblade-discovery/fixtures/linux-ambiguous.json
  - tools/openblade-discovery/fixtures/linux-incomplete.json
  - tools/openblade-discovery/fixtures/linux-no-candidates.json
  - tools/openblade-discovery/src/linux.rs
  - tools/openblade-discovery/src/macos.rs
  - tools/openblade-discovery/src/main.rs
  - tools/openblade-discovery/src/snapshot.rs
---

## Decided
Draft PR 905 proposes only the developer macOS discovery CLI and synthetic macOS snapshot. The user's scope correction defers the Linux adapter and any SwiftUI implementation.

## Tried and ruled out
The combined development checkpoint d315b58ad8c916540513123fd5fcb9d599962773 remains reachable in the branch history. The current proposal removes its Linux adapter, fixtures and discovery checks without rewriting history.

## Watch out
The pre-existing Linux Rust matrix must continue unchanged. Discovery-specific checks only run on Intel and Apple silicon macOS. Keep Windows payload exclusions and the unknown exact-model boundary.
