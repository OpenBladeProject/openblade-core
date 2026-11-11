---
title: Preserve safe rollback and fresh native recovery proofs
author: Jack
date: 2026-11-11
branch: fix/native-recovery-lifetime
touches:
  - docs/rust-native-origin-corrections.md
  - src/OpenBlade.Deployment/WindowsNativeAttemptHistory.cs
  - src/OpenBlade.Deployment/WindowsNativeEpoch.cs
  - src/OpenBlade.Deployment/WindowsNativeEpochFiles.cs
  - src/OpenBlade.Deployment/WindowsNativeInstalledSelection.cs
  - src/OpenBlade.Deployment/WindowsNativeRecoveryState.cs
  - src/OpenBlade.Setup/SetupCommandRunner.cs
  - src/OpenBlade.Setup/SetupNativePackageTransaction.cs
  - src/OpenBlade.Setup/WindowsSetupPlatform.cs
  - tests/OpenBlade.Deployment.Tests/NativeRecoveryTests.cs
---

## Decided
Recovery uses fresh current-phase/all-attempt proof snapshots under the writer and private exclusive custody lock. Immutable content-addressed snapshots preserve prior proofs and bounded canonical partial staging files. New custody locks are protected SYSTEM/Admin only before publication; legacy public locks are refused without ACL repair. Both languages share 4096 admitted attempts with drain capacity reserved. Typed expected STOP is normalized only after real shutdown before combining cleanup results; sticky trust loss and cleanup/report/lost-channel failures remain fatal. Verified managed predecessors restore their captured fixed service path, running/stopped state, and managed Agent startup independently of incoming native selection. Native retirement and uncertain startup remain fenced for either incoming backend. Start ambiguity begins directly before StartService, after configuration and cancellation admission. GET receipt timestamps precede waiting on the observer mutex, retaining strict newer-completion freshness.

## Tried and ruled out
Do not infer historical drain from a later original clean stop, process exit, reboot, or custody release. Current markers do not independently enforce metadata-only composition; unmatched attempts remain refused until a separate authenticated versioned capability/recovery contract. A single create-once proof cannot survive a legitimate later launch/phase change. Blanket native-transaction rollback refusal loses safe managed restoration. Restoring service through incoming native selection or restoring Agent sign-in through provisional/unresolved selection is invalid. Shutdown-first polling alone misses ingress before signal delivery; generic PermissionDenied/stop flags cannot classify expected STOP.

## Watch out
This refines the e1c6aed9 origin/status stage and its learning: submission is published immediately before StartService rather than before service configuration, and safe managed restoration is retained until actual native ambiguity. Ordinary managed-to-managed behavior stays enabled. Retirement intent fences new native startup; interrupted retirement metadata also needs fresh custody proof. Exhaustion trades another restart for explicit generation replacement, and private custody prevents only conflicting unprivileged lock reads, not general local DoS. All evidence here is offline, injected/private storage or static compile/PE evidence; no native roles, OS service/pipe/task probes, installation or hardware were executed. Required Rust core stays enabled and firmware remains denied.
