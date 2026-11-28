---
title: Retain the Windows writer lease in the managed user context
author: Jack
date: 2026-11-10
branch: feat/windows-managed-writer-lease
touches:
  - src/OpenBlade.Service/BatteryConfigurationStore.cs
  - src/OpenBlade.Service/PersistentOverrideStore.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/ThermalConvergenceStateStore.cs
  - src/OpenBlade.Service/UserConfigurationRepository.cs
  - tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.UserActivation.cs
  - tests/OpenBlade.WriterFence.Scratch/WriterLease.cs
  - src/OpenBlade.Service/UserConfigurationWriterScope.cs
  - tests/OpenBlade.Service.Tests/UserConfigurationWriterLeaseTests.cs
---

## Decided
The managed per-user repository may receive a verified exclusive handle for an existing SID root. Its context keeps the handle through legacy migration and every subsequent operation that can write. The runtime tracks unpublished activation candidates, owns the active context, refreshes an interrupted same-SID activation on its existing handle, and drains activations before shutdown retires that handle.

## Tried and ruled out
The instance migration semaphore alone ends before returned stores are used. Acquiring a second handle on same-SID retry conflicts with the runtime's own live handle. Disposing only the published context races an in-flight candidate and its operation gate. Native acquisition errors must use the existing configuration IO failure path.

## Watch out
Latch session-change ingress before waiting on the runtime gate. The optional scope serializes writable store calls and refuses pending recovery, while battery loads and saves retain their preservation checks. Scratch roots reuse the existing pinned NTFS directory and lock-file identity checks. Production injection is still absent. Verify protected provisioning, all competing writer paths including machine bootstrap, and independent SCM/process-generation retirement before enabling a cutover.
