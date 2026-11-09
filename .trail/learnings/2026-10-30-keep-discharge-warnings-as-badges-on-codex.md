---
title: Keep discharge warnings as badges on existing tray modes
author: Codex
date: 2026-10-30
branch: feat/plugged-in-discharge-warning
touches:
  - src/OpenBlade.Tray.Core/TrayIconState.cs
  - src/OpenBlade.Tray.Core/TrayIconAssetPath.cs
  - src/OpenBlade.Tray.Core/MainViewModel.ExternalPowerDischarge.cs
  - src/OpenBlade.Tray/WinFormsTrayIconHost.cs
  - build/Generate-ExternalPowerDischargeIcons.ps1
---

## Decided

Battery discharge while plugged in is an additive tray badge. Preserve the center mode glyph and the lower-left AC or USB-C source marker; add the shared downward-arrow badge at the lower right. This replaces the center-arrow design decision recorded in 2026-10-30-extend-the-approved-tray-family-for-codex.md after user review found that it hid the active mode.

Keep TrayIconState as the base mode/source. IsExternalPowerDischarging is independent presentation state. Resolve a badged asset for each admitted external-power state, including HyperBoost, and refresh the native icon when either state or taskbar theme changes. GenericError, unverified/unknown modes and battery states retain their existing presentation. Detection, episode tracking and notification preference remain unchanged.

## Tried and ruled out

Replacing the mode glyph with a central discharge arrow loses mode information. An independent warning enum state has the same conflict. Use twelve composed ICO variants instead: six external-power bases in two themes, each with all five native sizes.

## Watch out

Compose onto the exact decoded native base pixels; rerasterizing or scaling the mode/source changes approved artwork. Clear only the badge contour. Validate both themes at 16, 20, 24, 32 and 48 pixels, preserve the original base ICOs, and test discharge/recovery plus a mode change while discharging. The host must observe both properties and dispose replaced native icons. A transient unknown reading must not reset an episode or badge a GenericError icon.
