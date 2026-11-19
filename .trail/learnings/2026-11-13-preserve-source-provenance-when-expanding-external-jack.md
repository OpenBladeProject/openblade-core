---
title: Preserve source provenance when expanding external power modes
author: Jack
date: 2026-11-13
branch: feat/all-external-power-modes
touches:
  - build/Build-NativeDevelopment.ps1
  - build/NativePayload.ps1
  - src/OpenBlade.Contracts/PerformanceShortcutSelection.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.HyperBoost.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - src/OpenBlade.Protocol/ProductionBatteryPort.cs
  - src/OpenBlade.Service/HyperBoostOwnershipJournalStore.cs
  - src/OpenBlade.Service/ProfileStore.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Tray.Core/MainViewModel.cs
---

## Decided
Offer every exact-model-supported performance preset on all confirmed external sources, including limited power, Custom and HyperBoost. Keep the actual adapter class and all existing write admission. HyperBoost restoration keeps exact entry intent on the same source, but an observed real source boundary durably expires that intent in favor of the current destination profile. The source-boundary marker is monotonic even across ABA and RestorePending recovery, and must be committed before another mutation.

## Tried and ruled out
Do not widen independent manual-fan or voltage capabilities as a side effect. Do not use current source equality alone to restore an old HyperBoost intent after ABA. Do not default dormant USB-C Custom settings during unrelated profile saves. Legacy frozen native parity fixtures remain immutable; tests assert the intentional new policy delta.

## Watch out
Keyboard shortcuts and UI pickers share the external-source policy and use the selected source's Custom intent. Windows external presence still requires fresh firmware classification per write. Direct HyperBoost exit binds one actual external class across the whole command sequence. A shared Cargo cache needs one cross-process lease through build, artifact copy and provenance; Cargo's build lock alone ends too early.
