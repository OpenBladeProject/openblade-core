---
title: Recover mailbox transfers through immutable terminal evidence
author: Jack
date: 2026-11-11
branch: fix/native-mailbox-corrections
touches:
  - docs/windows-native-origin-status.md
  - src/OpenBlade.Deployment/WindowsManagedMailboxHandoff.cs
  - src/OpenBlade.Deployment/WindowsManagedMailboxPackage.cs
  - src/OpenBlade.Deployment/WindowsNativeEpoch.cs
  - src/OpenBlade.Deployment/WindowsNativeEpochFiles.cs
  - src/OpenBlade.Deployment/WindowsNativeInstalledSelection.cs
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - src/OpenBlade.Deployment/WindowsNativeTrustLifecycle.cs
  - src/OpenBlade.DevelopmentDeployment.Setup/DevelopmentDeploymentProvisioner.cs
  - src/OpenBlade.Protocol/ProductionLightingPort.cs
---

## Decided

Resolve a mailbox handoff through immutable per-request selection, original run/build/request/receipt/exit evidence, exact epoch intent/mapping and a custody-produced terminal proof. Preserve every older byte. New observation requires fully proven retirement, a new authenticated managed original and a new request/receipt. An intact ffc legacy transfer may use its separately pinned positive exit-before-binding contract; incomplete or unsupported legacy history stays fenced.

Anchor optional handoff creation below installer-protected machine data. A positive creation intent and completion distinguish recoverable bootstrap from unknown storage. Keep native custody admission mandatory. First enrollment has its own positively unenrolled creation path; signed trust maintenance still retains exclusion.

Origin trust and reader availability are separate. No-worker attempts can serve diagnostic metadata and drain only their own transport. Read revocation stays sticky. Retain thermal presentation for five monotonic seconds from getter completion, with current metadata and no freshness/capabilities. Track actual owner-close tasks before sealing, with caller-budget waiting and explicit resource roots on failure.

## Tried and ruled out

Global file presence as permanent active state; absent files or tombstones as retirement; receipt remapping; direct native-to-native renewal; SCM Stopped as exit/drain; metadata drain for observation artifacts; copying stale deviceVerified; metadata refresh renewing thermal age; bounded disposal waiters as actual owner retirement; late completion publishing a receipt; replaying ambiguous keyboard release during close.

## Watch out

Mapping must follow authenticated origin/policy/custody construction. Preserve both halves and reconcile the same intent after publication cuts. Keep C#/Rust process and ordinal bounds identical. A retained-original wait needs a monotonic post-sample deadline check. Keep the actual closing provider/port rooted after faulted task state is released. Compile, list and classify exact safe Rust bodies before execution; role, process/token/session/SCM/task/private-pipe probes remain outside offline authority. This correction stage is not full Windows backend parity or physical proof.

Trail exact-base review reported twenty lexical matches in the two guides. The changed text preserves the quoted policies: signed maintenance keeps exclusion; first creation is positively unenrolled; native custody and observation transport drains remain mandatory; original exit never replaces worker retirement; current/frontier commit publication is unchanged; no model writes or OS probes are added. Negated shortcuts in the new guide are prohibitions, not reintroductions. The default-main check was unrelated history, so the correction was checked against its pinned ffc base instead.
