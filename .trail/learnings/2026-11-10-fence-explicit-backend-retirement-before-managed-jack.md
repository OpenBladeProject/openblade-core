---
title: Fence explicit backend retirement before managed startup writes
author: Jack
date: 2026-11-10
branch: fix/phase-8n-startup-retirement
touches:
  - src/OpenBlade.Service/BatteryConfigurationStore.cs
  - src/OpenBlade.Service/MachineConfigurationWriterBinding.cs
  - src/OpenBlade.Service/MachineProfileBootstrapper.cs
  - src/OpenBlade.Service/PersistentOverrideStore.cs
  - src/OpenBlade.Service/ServiceRunMarkerStore.cs
  - src/OpenBlade.Service/ServiceStartupCoordinator.cs
  - tests/OpenBlade.Service.Tests/ServiceStartupCoordinatorTests.cs
---

## Decided
- When coordinator startup is an explicit backend transition, verify the outgoing generation and clear marker/recovery evidence under the exact enrolled writer lease before either configuration load can persist anything.
- Carry the same lease through aggregate bootstrap, override normalization and successor marker publication. Each store keeps its scope and store gate ordering. Release the lease before power capture, runtime initialization or hardware work.
- Ordinary startup still loads the configuration before classifying the prior run marker. Direct marker-only calls keep their existing retirement callback contract.

## Tried and ruled out
- A preflight followed by independent lease acquisitions leaves a gap between proof and mutation. Reacquiring the native lock while it is held would self-contend.

## Watch out
- The configuration stores and marker must agree on root and provider. A successful callback from a different composition cannot authorize writes.
- Refused transitions preserve the existing files. Once admitted, preserve the aggregate's rollback fence and any unfinished marker publication. A later runtime failure retains the successor marker for ordinary recovery.
