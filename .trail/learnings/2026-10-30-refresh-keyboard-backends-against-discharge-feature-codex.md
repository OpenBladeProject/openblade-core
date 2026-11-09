---
title: refresh keyboard backends against discharge feature
author: Codex
date: 2026-10-30
branch: feat/keyboard-input-backends
touches:
  - CHANGELOG.md
  - Directory.Build.props
  - build/Generate-ExternalPowerDischargeIcons.ps1
  - src/OpenBlade.Tray.Core/ExternalPowerDischargeTracker.cs
  - src/OpenBlade.Tray.Core/MainViewModel.ExternalPowerDischarge.cs
  - src/OpenBlade.Tray.Core/MainViewModel.cs
  - src/OpenBlade.Tray.Core/NotificationPreferences.cs
  - src/OpenBlade.Tray.Core/Properties/Resources.qps-ploc.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.ru-RU.resx
---

## Decided

Refresh PR 867 from main after PR 866 even when the previous head's CI was green. Both features independently advanced 0.46.0 to 0.47.0, so the keyboard feature now advances the current main baseline from 0.47.0 to 0.48.0. A green version check is valid only for its inspected base.

Preserve both Unreleased bullets and their original implementing PR links. Keep all keyboard backend and external-power discharge strings in each of the four Tray locales. The resource conflicts share the final closing data tag outside the conflict block; add the first block's closing tag before retaining the other block.

## Tried and ruled out

Choosing either side would lose a feature's localization. Removing conflict markers and concatenating XML blindly would nest data elements incorrectly. Verified the resolved resource map against all three Git stages: preserve each side's new keys and choose a changed common value only when the other side still matches the base. Every locale has 952 unique keys.

## Watch out

The automatic MainWindow and shell-test merges retain both independent surfaces. A read-only integration review confirmed both keyboard selector/status paths and discharge notification/observation paths. A refreshed head needs its own validation and CI before merge; do not treat the prior head's successful pipeline as a result for the combined tree. Keep local evidence outside the managed worktree before eventual archive.
