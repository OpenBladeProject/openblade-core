---
title: Keep independent audit slices on main for required CI
author: Jack
date: 2026-11-04
branch: fix/codebase-reliability-audit
touches:
  - ".github/workflows/ci.yml"
  - ".github/workflows/pr-version.yml"
  - build/benchmarks/BackgroundWorkloads/Program.cs
  - src/OpenBlade.Contracts/ThxAudioProfiles.cs
  - CHANGELOG.md
---

## Decided
All reliability-audit implementation PRs target main because CI, version validation and developer-identity workflows only run for main-target pull requests. PR876 owns shared recovery and background workers; PR877 owns provisioning; PR878 owns developer validation; PR874 retains tray/UI fixes. This supersedes the stacked-PR suggestion in the earlier evidence-compaction learning.

## Tried and ruled out
Stacking the tray PR on core would skip required CI. Both main-target slices include the same unchanged THX profile equality implementation and regression test because each depends on it. Their contents are identical, not alternate implementations. Keep BackgroundWorkloads with core: its reflection-based benchmark needs the new asynchronous GPU sampling seam even though the dependency is not checked by compilation.

## Watch out
Preserve the union of source/test blobs from archived snapshot 0ba5840e45fa6ba80183ab1a2c9c397295f3b99f. Refresh each product PR version against main before merging, and retain only its own changelog claims. Bulky results remain available by immutable archive links. Open PR branches and the evidence archive are not merged-branch cleanup targets.
