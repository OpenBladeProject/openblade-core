---
title: Bind and retain the complete scratch receiver closure
author: Codex
date: 2026-11-10
branch: fix/phase10b-receiver-closure
touches:
  - src/OpenBlade.DeploymentBroker/ScratchReceiverClosure.cs
  - src/OpenBlade.DeploymentBroker/ScratchCandidateLaunch.cs
  - src/OpenBlade.Deployment/WindowsCandidateReceiptStorage.cs
  - tests/OpenBlade.Setup.Tests/ScratchCandidateLaunchOfflineTests.cs
  - tests/OpenBlade.Setup.Tests/WindowsCandidatePackageReceiptTests.cs
---

The framework-dependent scratch apphost digest does not identify its receiver
DLL, transitive assemblies, satellite resources, Windows RID assets or loader
configuration. Retain and bind the complete declared app-local closure before
launch and through receipt, activation and commit. Seal its directory namespace
against transient loader additions; directory rename retention alone is
insufficient. Restore the original scratch DACLs through retained handles, attempt
every cleanup, and retain both the original failure and restoration failures.

The issuer-refusal test reaches Bind with the existing current process at a wrong
image and proves no evidence, in-flight or receipt mutation. The explicitly named
scratch-files-only Reopen test authenticates earlier launch evidence over changed
canonical in-flight bytes, then reaches the original pending-receipt comparator.
The actual build closure and resource lifetimes are tested without a child launch
or IPC. These tests establish offline fixture properties; they do not establish
an installed/Burn handoff, shared-runtime identity or hardware behavior.
