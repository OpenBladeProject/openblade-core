---
title: Package a dormant Rust DLL without activating Protocol consumers
author: Jack
date: 2026-11-08
branch: build/rust-native-package
touches:
  - ".github/workflows/build.yml"
  - ".github/workflows/ci.yml"
  - ".github/workflows/pr-version.yml"
  - Directory.Build.props
  - build/CiImpactManifest.json
  - build/Publish.ps1
  - build/StoreSubmissionValidation.ps1
  - docs/rust-boundaries.md
  - rust/README.md
  - tests/Build.Tests/StoreSubmissionValidation.Tests.ps1
---

## Decided
Stage 5 ships a dormant first-party Windows x64 Rust DLL only through explicit Publish.ps1 packaging. Keep ordinary .NET builds and product dependencies independent of Rust. Use static CRT, actual PE import/version inspection, source-bound hashes and the existing optional Authenticode policy. Sign before installer embedding and retain separate unsigned/final hashes. Enable pure C#/Rust differential tests only through OpenBladeNativeShadow=true and the explicit Compare-Shadow.ps1 entrypoint. Extend existing .NET CI and version gates to stacked PR bases.

## Tried and ruled out
Do not attach the DLL to Protocol's project dependency graph: Service, Capture and FirmwareSmoke consume Protocol and need a separately reviewed later runtime handoff. No product loading, hardware calls, IPC/schema change, live install, restart, or new trust root. Packaging was intentionally deferred by stages1-4; this assigned stage authorizes dormant packaging only. Shipping the payload changes packaging, so VERSIONING.md requires patch0.56.0 to0.56.1.

## Watch out
Windows PowerShell5.1 lacks Path.IsPathFullyQualified. The pinned minimal Rust toolchain distributes COPYRIGHT-library.html and licenses/*.txt, not the historic COPYRIGHT/LICENSE-MIT paths. Keep source paths out of shipped provenance; record remapping arguments symbolically. Native source inputs must be committed before source-bound release packaging. CI must run the actual .NET installer lane for stacked PRs, not only Rust's four native lanes. Preserve original Claude-reviewed SHA separately from any later tested fixes.
