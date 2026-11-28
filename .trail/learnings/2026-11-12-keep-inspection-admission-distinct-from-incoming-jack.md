---
title: Keep inspection admission distinct from incoming mailbox composition
author: Jack
date: 2026-11-12
branch: fix/mailbox-history-admission
touches:
  - src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs
  - src/OpenBlade.Deployment/ManagedMailboxRecovery.cs
  - src/OpenBlade.Deployment/WindowsManagedMailboxHandoff.cs
  - src/OpenBlade.Deployment/WindowsManagedMailboxPackage.cs
  - src/OpenBlade.Deployment/WindowsNativeEpoch.cs
  - src/OpenBlade.Deployment/WindowsNativeInstalledSelection.cs
  - src/OpenBlade.Deployment/WindowsNativeTrustLifecycle.cs
  - src/OpenBlade.Setup/Program.cs
  - src/OpenBlade.Setup/SetupNativePackageTransaction.cs
  - tests/OpenBlade.Deployment.Tests/ManagedMailboxCorrectionTests.cs
---

## Decided
Package-aware Setup preflight rejects an incompatible verified incoming package before runner mutations. A null incoming package denotes existing-state inspection or uninstall and still authenticates unresolved retained preparation history; it does not create an epoch or grant Begin authority. Setup Program's install branch always passes its verified broker package.

High-level Begin and Setup selection now inject only internal storage resolvers. Their production and fixture default legacy adapter composition is identical; tests cannot replace admission with an authentication-success callback at these entry points. Existing lower-level algorithm fixtures remain separate from production composition coverage.

## Tried and ruled out
Rejecting all null incoming selections would unnecessarily block uninstall of authenticated retained canceled preparations. Keeping high-level authentication-result callbacks was unnecessary once private storage could exercise the same adapters.

## Watch out
Default production resolvers retain protected real-root and security-policy checks. Offline tests supply private roots and must never invoke those real-root resolvers. A live fixture enrolled run recovers history through the same AcquireRunExclusion flow before startup releases native proof locks; its lifetime exclusion stays held during pre-stop selection.
