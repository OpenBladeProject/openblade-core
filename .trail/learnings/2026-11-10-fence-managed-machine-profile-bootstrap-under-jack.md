---
title: Fence managed machine profile bootstrap under one verified root
author: Jack
date: 2026-11-10
branch: feat/windows-machine-bootstrap-writer
touches:
  - src/OpenBlade.Service/BatteryConfigurationStore.cs
  - src/OpenBlade.Service/MachineProfileBootstrapper.cs
  - tests/OpenBlade.Service.Tests/BatteryConfigurationStoreTests.cs
  - tests/OpenBlade.Service.Tests/MachineProfileBootstrapperTests.cs
  - tests/OpenBlade.Service.Tests/UserConfigurationWriterLeaseTests.cs
---

## Decided
The managed machine bootstrapper can opt in to the existing Windows writer scope with a verified exclusive lease for the exact machine directory. The store admits the three fixed sibling targets, rejects a cross-root or non-file layout, and retains the lease through recovery probing, materialization, readback, rollback and cleanup. Lock order is the native root and lease, scope gate, then aggregate gate. Release in reverse order. Transaction siblings on this path are created exclusively and only names created by this attempt are cleaned up.

## Tried and ruled out
A battery store Load call can materialize safe defaults, migrate a profile-only legacy document or rewrite an older committed aggregate. Treating it as a reader misses the durable writes. Acquiring the per-user lease cannot fence the machine directory. Moving the fence to general service startup would imply coverage of later writes without admitting them.

## Watch out
The public service composition still has no machine lease provider. The scratch primitive pins the directory chain and opens the same enrolled lock as Rust. A production provider still needs protected namespace and ACL verification, coverage of other writers, and independent process retirement evidence. Startup override migration and the run marker, runtime fallback aggregate and override saves, and setup or specialty journals do not use this bootstrap lease. Per-user migration reads the machine legacy files and writes under its own SID root. The bootstrap fence does not change that reader.
