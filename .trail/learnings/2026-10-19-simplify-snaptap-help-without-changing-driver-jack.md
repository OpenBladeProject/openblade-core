---
title: Simplify SnapTap help without changing driver admission
author: Jack
date: 2026-10-19
branch: fix/snaptap-info-copy
touches:
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.Tray/Strings
  - Directory.Build.props
  - CHANGELOG.md
---

## Decided
Simplify SnapTap_InfoInput in XAML and English to "SnapTap uses the Razer keyboard driver for the laptop’s built-in keyboard." Remove the redundant RZ09-0581 sentence in Russian and Chinese and regenerate qps-ploc. Keep game compatibility wording and actual model/driver admission unchanged.

## Tried and ruled out
Trail check flags SnapTap wording and product 0.37.1 as lexical conflicts with Blade 18 backend and native THX version learnings. This text-only change adds no backend, admits no DLL and does not restore literal English audio terminology.

## Watch out
The existing laptop-snap-tap-info snapshot route completed in all four locales but RenderTargetBitmap returned blank popup images on this host; main-page images rendered. Do not claim that success exit codes prove the popup was visually reviewed.
