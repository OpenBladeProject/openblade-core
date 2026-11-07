---
title: retain unknown battery fields and independent new preset associations
author: Jack
date: 2026-11-07
branch: feat/official-source-reviewed-support
touches:
  - src/OpenBlade.Contracts/Profiles.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - src/OpenBlade.Service/BatteryOperationPlanner.cs
  - src/OpenBlade.Service/ControlCoordinator.cs
  - src/OpenBlade.Service/PendingWriteConfirmationTracker.cs
  - src/OpenBlade.Service/ProfileStore.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/WindowsVerifiedBladeDeviceProvider.cs
  - src/OpenBlade.Service/WriteConfirmationPolicy.cs
  - src/OpenBlade.Tray.Core/MainViewModel.cs
---

## Decided

Official model support and physical validation are separate statuses. Extend exact-model source-reviewed profiles without borrowing another laptop's commands, driver stack or physical results.

Ordinary battery charge-policy recovery confirms only the fields changed by that typed operation. Profiles without a reviewed temporary-full-charge query retain an unknown override value. Carry an explicit PolicyOnly confirmation scope for ordinary limiter changes; the default All scope remains strict for override operations, cleanup and unknown callers. Pending or deferred All obligations must not be narrowed. Resume reconciles read-only and never replays an ambiguous write.

Turbo and Maximum Power have independent optional Windows power-mode association properties. Preserve the original six-argument constructor and null defaults for older writers. Legacy schema migrations iterate the original six roles explicitly so future enum additions cannot overwrite existing associations.

## Tried and ruled out

Do not synthesize temporary-full-charge=false from an unsupported query capability. Requiring its readback for ordinary limiter writes permanently prevents recovery, while globally dropping the requirement weakens real override recovery. Do not alias new performance roles onto the existing Performance preference: this couples user settings and can clear legacy data during migration.

## Watch out

Keep actual override operations blocked when override readback is unknown. Apply the nullable ordinary-policy rule consistently in protocol recovery, provider state, service planning, confirmation tracking and UI gates. IPC 73 adds two numeric enum values plus two optional association members; preserve IPC 72's golden to review the exact change. The new four model profiles have no new driver or THX runtime admission merely because the central supported-model list grew.
