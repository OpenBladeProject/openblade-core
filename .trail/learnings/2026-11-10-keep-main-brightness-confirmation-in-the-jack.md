---
title: Keep main brightness confirmation in the Windows Rust fixture refresh
author: Jack
date: 2026-11-10
touches:
  - AGENTS.md
  - CHANGELOG.md
  - Directory.Build.props
  - src/OpenBlade.Display/AssemblyInfo.cs
  - src/OpenBlade.Display/WindowsDisplayBrightness.cs
  - src/OpenBlade.Display/WindowsDisplaySettingsController.cs
  - src/OpenBlade.SessionAgent/FunctionRowActionExecutor.cs
  - src/OpenBlade.SessionAgent/WmiDisplayBrightnessController.cs
  - src/OpenBlade.Tray.Core/ExternalPowerDischargeTracker.cs
  - src/OpenBlade.Tray.Core/MainViewModel.ExternalPowerDischarge.cs
---

## Decided

Refresh the private Windows Rust fixture stack with an exact two-parent local merge. Keep main 0.58.4's single pre-write brightness snapshot and monitor-path plus WMI-instance readback, alongside the migration's independent HDR projection. Check both role hashes against their pins and reuse the pinned fixture build when the Rust source, lockfiles and toolchain inputs are identical.

## Tried and ruled out

A shared file did not require a manual resolution. The merge's diff against main in the display controller contains only the HDR projection extraction. The Contracts and Session source trees are unchanged relative to both parents. Trail comparisons against either parent report wording matches in inherited changes. The new overlap does not reintroduce UInt32-only brightness acknowledgement or confirm from an unrelated display.

## Watch out

Signed scratch readiness and rollback exercise scripted peers in ordinary-user scratch storage. Private v79 Fan Stop exercises scripted ownership, cleanup and handback. Neither establishes native service registration, hardware handback, installed cutover or installed rollback.
