---
title: Fence ordinary machine configuration writes at the bound service stores
author: Jack
date: 2026-11-10
branch: feat/windows-machine-configuration-writer
touches:
  - src/OpenBlade.Service/BatteryConfigurationStore.cs
  - src/OpenBlade.Service/PersistentOverrideStore.cs
  - src/OpenBlade.Service/ProfileStore.cs
  - src/OpenBlade.Service/ServiceStartupCoordinator.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.cs
  - tests/OpenBlade.Service.Tests/ServiceStartupCoordinatorTests.cs
  - ".phase8j-scratch/Microsoft.NET.Workload_49636_20261110_044736_085.log"
  - restore-log.txt
  - src/OpenBlade.Service/MachineConfigurationWriterBinding.cs
  - tests/OpenBlade.Service.Tests/MachineConfigurationWriterTests.cs
---

## Decided
- Bind the ordinary machine profile, preferences, commit and override leaves at the service store constructors. Their existing startup and runtime entry points share the opt-in exact-root lease provider. Default composition remains unbound.
- Take native exclusion before the scope and store gates. Hold it through durable replacement, readback or rollback, then release it before returning to the runtime.
- Compare the loaded aggregate commit or exact override bytes on a subsequent save. An inspection for diagnostics or migration does not refresh the writer baseline.

## Tried and ruled out
- Coordinator-only and IPC-only fencing miss ordinary runtime or normalization writes. A lease across hardware reconciliation would outlive the persistence boundary.
- Trail check matches generic words from hardware and previous transaction learnings here. The relevant prior rules are retained: the configuration IO and quarantine paths fail closed, writes preserve unresolved artifacts, and every materializing load has admission.

## Watch out
- A current-schema override load can normalize and write. The aggregate recovery-required sidecar must block the override store as well as the aggregate.
- Legacy migration inspects the machine files and writes to the per-user destination. Its existing scope is separate. Startup reads the aggregate and overrides under separate leases.
