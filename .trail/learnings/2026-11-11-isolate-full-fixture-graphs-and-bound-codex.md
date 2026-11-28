---
title: Isolate full fixture graphs and bound scratch data cleanup
author: Codex
date: 2026-11-11
branch: fix/rust-fixture-isolation-cleanup
touches:
  - Directory.Build.props
  - src/OpenBlade.DeploymentBroker/ScratchLaunchData.cs
  - tests/Build.Tests/FixtureGraphIsolation.Tests.ps1
  - tests/OpenBlade.Setup.Tests/ScratchLaunchDataOfflineTests.cs
---

## Decided
Global opt-in native-package, candidate-receipt and package-proof fixture flags change referenced assemblies and restore graphs. Isolate the entire reachable managed graph before Common.props evaluates paths, with one short effective n/c/p/s identity per combination for both ordinary and artifacts layouts. Scratch-native receiver changes are effective with candidate receipt. Keep product default paths unchanged. Verify actual Setup copy-local PE metadata and normal/fixture restores in one shared artifacts root; Service/Session have no Deployment reference.

ScratchLaunchData owns the newly created exact private child through final-pin acquisition: on failure re-admit once and attempt only nonrecursive empty-directory deletion, or retain the exact path with original and cleanup errors. No child exists at this boundary. Cleanup shares one entry budget across the tree, caps lease depth, and checks deadline/cancellation between filesystem operations. Exhaustion retains remaining files, reports the exact root child and releases pins. Time checks cannot preempt blocked I/O.

## Tried and ruled out
Isolating Deployment alone leaves downstream Setup/Broker/DeploymentClient copies and Deployment.Tests restore state colliding. Long descriptive suffixes exceeded the NativeAOT Windows linker path limit; short documented flag keys passed. Manually separated external build roots mask the original defect. Natural-exit and termination-race Stop behavior and the apostrophe MSBuild expression were already correct; do not repair those rejected claims.

## Watch out
Storage's existing 508-character pin admission already bounds recursion; no fatal unbounded-stack reproduction was established. The missing cleanup bound was breadth/work and held resources. Keep exact-original-process retirement, stop-on-ambiguity, aggregate refusal calibration, zero-argument refusal, stderr ordering, default providers and IPC unchanged. Pure/injected/private-FS tests and compiler/link checks establish no actual child/job/channel/SCM, installed lifecycle or hardware result. Reuse unchanged native Service/Agent evidence under its original source identity.
