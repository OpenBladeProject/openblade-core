---
title: Review lexical Trail matches against the discovery pilot boundary
author: Jack
date: 2026-11-09
branch: feat/portable-hid-discovery
touches:
  - ".github/workflows/rust.yml"
  - build/PublishPayloadValidation.ps1
  - rust/README.md
  - rust/tools/openblade-discovery/.gitignore
  - rust/tools/openblade-discovery/Cargo.lock
  - rust/tools/openblade-discovery/Cargo.toml
  - rust/tools/openblade-discovery/README.md
  - rust/tools/openblade-discovery/fixtures/linux-ambiguous.json
  - rust/tools/openblade-discovery/fixtures/linux-incomplete.json
  - rust/tools/openblade-discovery/fixtures/linux-no-candidates.json
---

## Decided
This pilot uses an independent developer binary and fixture schema. It preserves all Windows IPC, production Rust library edges, product version and build gates.

## Tried and ruled out
Trail check flagged lexical overlaps with earlier Rust stage restrictions and the new privacy learning. The flagged CI builds and checks are additive; the raw PowerShell file read reads a fixed stderr diagnostic. The standalone Cargo package version is not the product version. Source references to HID uevent explain why only modalias is read.

## Watch out
Assess Trail matches against the scoped change and record the disposition. No reported host metadata is an admission decision.
