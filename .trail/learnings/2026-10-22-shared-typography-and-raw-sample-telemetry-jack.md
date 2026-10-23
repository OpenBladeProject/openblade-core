---
title: Shared typography and raw-sample telemetry analysis
author: Jack
date: 2026-10-22
branch: feat/telemetry-analysis-typography
touches:
  - AGENTS.md
  - Directory.Build.props
  - build/Capture-LocalizationSnapshots.ps1
  - build/localization/TranslatorScreenshotMap.json
  - docs/telemetry-and-battery-history.md
  - src/OpenBlade.Tray.Core/TelemetryHistory.cs
  - src/OpenBlade.Tray.Core/UiPreferenceStore.cs
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/App.xaml
  - src/OpenBlade.Tray/MainWindow.FeatureSnapshots.cs
---

## Decided
Use application-level semantic WinUI typography roles from Themes/Typography.xaml. Keep native title, section, body, caption, language fallback and text scaling. Table body cells, including numeric readings and counts, use regular body weight with tabular numerals; only headers use strong body text. Native string commands share the wrapping template and 40-DIP minimum height. Existing compact tray typography is an explicit migration exception.

Telemetry compares CPU/GPU temperatures, battery power and fan speeds in three unit-specific lanes sharing one time axis. The analysis table uses one nearest original sample for every At sample cell. Range statistics and missing counts use all raw samples in the inclusive selected window. Persist only normalized per-user visibility and RGB colors; service, Session Agent and IPC contracts stay unchanged.

The overview has captured edge resize and center panning, with precise offset-bearing keyboard fields. Default full range grows with incoming samples; edited or pinned ranges stay fixed. Pinning a sample must freeze both sample follow and range follow. Follow latest resumes a fixed-duration window. Reset the range for a different recording/session. Keep hidden-page snapshot/draw deferral and recording lifetime intact.

## Tried and ruled out
A single Y scale cannot compare degrees, watts and RPM honestly. Decimated plot points cannot provide precise inspection or statistics. Merely setting samplePinned does not stop range following: a sliding viewport can silently replace the pinned reading. Local G date formatting loses fractions and offset during editing; use round-trippable fields and leave unchanged text untouched. Clamp candidate timestamp ticks before constructing a DateTimeOffset so extreme replay ranges cannot overflow.

## Watch out
Use real disconnected native captures for populated, empty, narrow, dark, range and color/metric editors, plus translated and pseudo-localized text. Flyout screenshots use the native presenter helper; RenderTargetBitmap cannot capture Popup placement. Inventory every exported screenshot for translators. UI-only work does not authorize a full installed-service replacement. Preserve raw gaps, isolated observations and selected timestamps in the table context when scrolled.
