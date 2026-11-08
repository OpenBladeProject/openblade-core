---
title: Integrate Rust stages with current CPU payload and strict merge gates
author: Jack
date: 2026-11-08
touches:
  - ".github/workflows/build.yml"
  - ".github/workflows/rust.yml"
  - build/Build-NativeDevelopment.ps1
  - build/CiImpactManifest.json
  - build/NativePayload.ps1
  - build/Prepare-NativeToolchain.ps1
  - build/Publish.ps1
  - docs/developer-guide.md
  - docs/rust-boundaries.md
  - rust/Compare-Shadow.ps1
---

## Decided
Refresh the seven Rust migration stages on the 0.57.0 fan-stop/CPU-sensing main baseline and recalculate the dormant native packaging patch to0.57.1. Preserve the exact LHM source-built attribution/signing location and the first-party Rust readiness location. During activation, sign Rust before compiling its embedded hash and omit it from the later signing loop; retain LHM signing there. Keep the standalone Windows battery observer before native admission because it uses only Windows queries; all existing hardware paths still follow native admission. Merge in dependency order only after required exact-head gates and review audit. The user authorized integration/merges, but installation and hardware tests remain later work.

## Tried and ruled out
Do not re-sign the activated Rust payload after compiling its manifest hash. Do not drop LHM signing or its source-built metadata to resolve the packaging conflict. Do not pin the stale0.56.1 version, bypass the active strict build-and-test rule, or repeat Claude review of unchanged stage code merely for merge bookkeeping.

## Watch out
GitHub may retain a stale baseRefOid after the remote main branch advances. Explicitly editing the PR base to its intended branch refreshes that metadata; trigger a new body/version check before trusting it. Review/test SHAs remain separate. Trail check returns lexical matches against historical dormant-stage restrictions and unrelated model terms; inspect and explain rather than claiming a green result. Primary untracked notes/debug.log and unrelated worktrees are preserved.
