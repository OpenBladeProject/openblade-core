---
title: Extend the approved tray family for the discharge icon
author: Codex
date: 2026-10-30
branch: feat/plugged-in-discharge-warning
touches:
  - build/Generate-ExternalPowerDischargeIcons.ps1
  - src/OpenBlade.Tray/Assets/Brand/TRAY-ASSETS.md
  - src/OpenBlade.Tray/Assets/Brand/Tray/external-power-discharging.svg
---

## Decided
The plugged-in discharge icon extends the approved tray family: use its exact laptop silhouette, contour-cleared plug badge, neutral light/dark colors, and round negative-space stroke weight. A single downward arrow replaces the small battery rectangle. The Astra High worker implemented this redesign after the initial artwork was rejected for not fitting the current design.
Keep one ordered geometry definition for SVG and both ICOs so the vector source and native raster assets cannot drift. Comparison images must read actual ICO frames and include the existing approved icons at native 16 and 20 pixels, as well as all five supported sizes.

## Tried and ruled out
The initial amber empty-battery cutout had too much internal detail and departed from the existing palette and source-badge language. Merely recoloring that drawing would not address the full mismatch. Do not redesign the approved mode, application, installer, or notification icons as part of this correction.

## Watch out
The plug means external power for the combined AC/USB-C warning; the existing tooltip gives the precise battery-discharge meaning. Preserve the theme convention: light glyphs on a dark taskbar. Each ICO still needs 16, 20, 24, 32, and 48 pixel PNG frames. Keep detection, state precedence, notifications, and hardware admission unchanged for this visual correction; reuse their passing verification.
