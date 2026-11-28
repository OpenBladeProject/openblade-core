---
title: Repair epoch publication and scratch resource lifetime
author: Jack
date: 2026-11-11
branch: fix/rust-epoch-scratch-repairs
touches:
  - Directory.Build.props
  - src/OpenBlade.Deployment/WindowsNativeEpoch.cs
  - src/OpenBlade.Deployment/WindowsNativeEpochFiles.cs
  - src/OpenBlade.DeploymentBroker/ScratchChildChannels.cs
  - src/OpenBlade.DeploymentBroker/ScratchFixtureFailure.cs
  - src/OpenBlade.DeploymentBroker/ScratchNativeReceiverLaunch.cs
  - src/OpenBlade.Setup/SetupCommandRunner.cs
  - src/OpenBlade.Setup/SetupNativePackageTransaction.cs
  - tests/OpenBlade.Deployment.Tests/OpenBlade.Deployment.Tests.csproj
  - tests/OpenBlade.Deployment.Tests/WindowsNativeEpochTests.cs
---

## Decided
Validate artifact mapping conflicts under the epoch writer before publishing accepted package.bin. Recover a durable Retired intent in the same epoch through phase/frontier/current boundaries, without replaying startup/readiness or certifying native drain. Use SDK FileRenameInfoEx replace and POSIX flags for mutable current.bin so retained delete-sharing readers keep old bytes while fresh current/frontier admission rejects them.

Separate the one preparation attempt, possible payload activation and possible native startup in Setup. Preparation and pre-start payload failures may restore the captured managed lifecycle and dispose resources; mark native startup possible immediately before the platform service call and deny automatic fallback thereafter. Keep partial provisional/frontier evidence for explicit metadata recovery.

Give algorithm-test Deployment its own restore project identity and outputs: NuGet does not restore ProjectReference.AdditionalProperties variants. Use the real MSBuild Exec with normalized path arguments and NonInteractive. Recursively classify aggregate refusal leaves. Route both channel acquisition paths through the same ownership-before-initialization cleanup seam. Give each scratch launch fresh data outside code; only confirmed original-handle retirement permits checked nonrecursive cleanup, with the original directory pinned throughout content cleanup.

## Tried and ruled out
Distinct output paths alone failed NETSDK1004 because NuGet restored only the default project variant. The two completed conditional receiver findings were rejected by exhaustive callers: retain zero-argument refusal and existing concurrent stderr drain ordering. Do not add speculative transport fallback.

## Watch out
Private NTFS and injected cleanup tests do not prove OS job/channel/process lifetime or loaded origin. A retained current object is provenance, never current authority. Unconfirmed stop preserves and reports launch data. Native providers remain closed; package retirement does not release an unresolved owner fence. Preserve failed build evidence and use exact-base Trail dispositions.
