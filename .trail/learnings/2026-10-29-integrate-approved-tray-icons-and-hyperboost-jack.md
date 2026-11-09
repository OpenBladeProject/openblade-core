---
title: Integrate approved tray icons and HyperBoost state
author: Jack
date: 2026-10-29
branch: feat/tray-icon-redesign
touches:
  - src/OpenBlade.Tray.Core/TrayIconState.cs
  - src/OpenBlade.Tray.Core/MainViewModel.cs
  - src/OpenBlade.Tray/OpenBladeIconAssets.cs
---

## Decided
Use the approved negative-space tray icons, with centered Balanced S, original smooth Performance curve and a dedicated AcHyperBoost state. HyperBoost status takes precedence over the underlying known AC mode. Update both snapshot presentation and the successful typed-command result so the icon changes without waiting for another status event.

## Tried and ruled out
Do not change application, installer or notification artwork. Do not map active HyperBoost to Performance/Custom or change Contracts merely to represent this UI state.

## Watch out
Keep unknown and inconsistent power/mode combinations on generic error, and preserve existing enum numeric values. Tray-only installation eligibility must compare the installed shared/IPC baseline with the target, not only the patch diff; a coordinated update is required when those baselines differ.
