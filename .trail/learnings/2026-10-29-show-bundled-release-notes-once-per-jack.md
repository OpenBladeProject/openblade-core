---
title: Show bundled release notes once per user and version
author: Jack
date: 2026-10-29
branch: feat/whats-new
touches:
  - Directory.Build.props
  - build/Capture-LocalizationSnapshots.ps1
  - build/localization/TranslatorScreenshotMap.json
  - src/OpenBlade.Tray.Core/OpenBlade.Tray.Core.csproj
  - src/OpenBlade.Tray.Core/UiPreferenceStore.cs
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/App.xaml.cs
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - src/OpenBlade.Tray/Strings/en-US/Resources.resw
---

## Decided
Show the current build's bundled changelog offline in a native ContentDialog, automatically once per user and release identity and manually from About. Strip informational commit metadata from the seen marker while keeping nightly revision and attempt identities. Use an optional UI preference so old files migrate without losing settings. Pending notes participate in the initial visibility decision; a hidden launch with no pending dialog must still start automatic update checks.

## Tried and ruled out
Do not fetch release notes over the network on launch, reuse another dated release when Unreleased is empty, or mark notes seen before a live dialog completes. About's two actions use ContextualToolbar and ContentWrapPanel so localized labels can wrap. The snapshot route renders the dialog itself because the window content snapshot excludes popup roots.

## Watch out
This is a new user-facing capability, so classify it as minor and recalculate from current main before merging; historical icon-patch version notes are not permanent pins. The existing layout drain is used after the dialog Opened event for a new surface, not as a workaround for nondeterministic screenshots. Trail lexical warnings about older telemetry tests or command styling do not apply to this release-note flow. Localized chrome uses en-US, zh-CN, ru-RU and qps-ploc resources; bundled changelog entries remain English.
