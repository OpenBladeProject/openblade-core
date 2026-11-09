---
title: Keep the discovery crate outside native source provenance
author: Jack
date: 2026-11-09
branch: feat/portable-hid-discovery
touches:
  - ".editorconfig"
  - ".gitattributes"
  - ".github/workflows/rust.yml"
  - rust/README.md
  - tools/openblade-discovery/.gitignore
  - tools/openblade-discovery/Cargo.lock
  - tools/openblade-discovery/Cargo.toml
  - tools/openblade-discovery/README.md
  - tools/openblade-discovery/fixtures/linux-ambiguous.json
  - tools/openblade-discovery/fixtures/linux-incomplete.json
---

## Decided
Keep the discovery crate at tools/openblade-discovery, separate from the production Rust tree.

## Tried and ruled out
A nested Cargo workspace under rust did keep the binary out of builds, but Windows native provenance lists all committed rust paths.

## Watch out
The Rust workflow explicitly selects tools/openblade-discovery changes; build from rust with the sibling manifest path to use the pinned toolchain. The Windows payload footprint test guards both developer CLI binary names.
