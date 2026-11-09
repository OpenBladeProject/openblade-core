---
title: Keep Labs cards grouped by their original sections
author: Jack
date: 2026-11-06
branch: feat/labs-sidebar
touches:
  - CHANGELOG.md
  - Directory.Build.props
  - build/Capture-LocalizationSnapshots.ps1
  - build/localization/TranslatorScreenshotMap.json
  - docs/keyboard-lighting-file-format.md
  - docs/ru-RU/keyboard-lighting-file-format.md
  - docs/zh-CN/keyboard-lighting-file-format.md
  - src/OpenBlade.Tray.Core/AppearancePreferences.cs
  - src/OpenBlade.Tray.Core/MainViewModel.cs
  - src/OpenBlade.Tray.Core/UiPreferenceStore.cs
---

## Decided
Labs is an always-present, reorderable sidebar page. Keep moved cards under their original section names and reuse the corresponding localized headings. Preset import and 60 Hz lighting both came from the Lighting page's Keyboard section. Preserve the regular Lighting page as the place to save an imported effect into a power profile.

## Tried and ruled out
A flat list or separate invented categories obscures the source grouping. The old Labs visibility flag is no longer needed. The preference reader accepts old files with that extra property.

## Watch out
Keep actual device admission, Windows Dynamic Lighting ownership, preset validation and confirmation, and advertised-rate checks. Route new sidebar destinations through the canonical navigation collection and append them during old-order normalization. Validate available and unavailable controls with isolated WinUI previews.
