---
title: Verify status prototype review fixes through production codecs
author: Jack
date: 2026-11-08
branch: feat/rust-status-cli
touches:
  - build/CiImpactManifest.json
  - rust/crates/openblade-status-cli/README.md
  - rust/crates/openblade-status-cli/fixtures/README.md
  - rust/crates/openblade-status-cli/src/transport/windows.rs
  - rust/crates/openblade-status-cli/src/wire.rs
  - rust/crates/openblade-status-cli/src/wire/tests.rs
  - tests/Build.Tests/CiBuildScope.Tests.ps1
  - tests/OpenBlade.Contracts.Tests/RustStatusCliContractTests.cs
---

## Decided
The completed isolated Claude Opus 5.5/high review covered candidate 45cb279b223c1048371879f3ba2068dcb0e89e43, run 090b51233203459aa7ab791865a523a0, success_native_review. Post-review fixes reject non-null success errorCode, distinguish cancellation-handler setup failure, harden Unicode duplicate aliases, exercise the real C# compatibility reader/writer, remove an unrelated current-IPC assertion, and route Rust request/response fixture edits into Contracts.Tests.

## Tried and ruled out
Keep v51: current v78 GetStatus can activate profiles, while v51 intentionally skips activation. Its existing temporary managed thermal-admission lease remains documented; a new passive service operation exceeds this existing-GetStatus prototype. Keep ID/version matching before trusting a service rejection. Pinned-toolchain Clippy and all four native CI lanes disproved the predicted formatting failure. The 9-second timeout test verifies actual production deadlines without adding runtime test-only features.

## Watch out
The reviewed candidate is not the later fixed head. Record both; no duplicate Claude run is needed or authorized after the one completed review. Unicode uppercase comparison is intentionally conservative for expanding mappings; current wire names are ASCII and values are not normalized. No live sample timestamp exists; keep cached provenance/freshness unknown.
