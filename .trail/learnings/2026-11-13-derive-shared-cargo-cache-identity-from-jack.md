---
title: Derive shared Cargo cache identity from Git common directory
author: Jack
date: 2026-11-13
branch: feat/all-external-power-modes
touches:
  - build/Build-NativeDevelopment.ps1
  - build/NativePayload.ps1
  - tests/Build.Tests/NativePayload.Tests.ps1
---

## Decided
Resolve shared Cargo cache identity from the main Git common directory in build/NativePayload.ps1 and reuse that helper in build/Build-NativeDevelopment.ps1. Differently named linked worktrees and short junction views must share both one target directory and one publication mutex. Keep branding verification and build/copy/provenance serialization intact.

## Tried and ruled out
Do not hard-code a machine checkout's former product name in build scripts: publishing runs branding verification and rejects it. Do not derive cache identity from the current worktree leaf: each worktree or short view would allocate another cache. Do not weaken branding checks or split the cache to work around publishing.

## Watch out
The helper itself is covered by existing NativePayload.ps1 source-provenance hashing. The focused packaging fixture creates only a tiny data-only linked checkout and verifies common cache and mutex identity. Reuse unchanged runtime test evidence for this build-only correction, and perform the real publisher validation before installation.
