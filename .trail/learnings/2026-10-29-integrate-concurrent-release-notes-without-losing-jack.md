---
title: Integrate concurrent release notes without losing live reorder isolation
author: Jack
date: 2026-10-29
branch: feat/live-layout-reorder
touches:
  - AGENTS.md
  - CHANGELOG.md
  - Directory.Build.props
  - build/Capture-LocalizationSnapshots.ps1
  - build/localization/TranslatorScreenshotMap.json
  - src/OpenBlade.Tray.Core/OpenBlade.Tray.Core.csproj
  - src/OpenBlade.Tray.Core/WhatsNewRelease.cs
  - src/OpenBlade.Tray.Core/WhatsNewSeenStore.cs
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/App.xaml.cs
---

## Decided
When the concurrent what's-new feature merged while live-reorder CI was running, refresh the branch against its exact main commit before merging and recalculate the minor version from0.45.0 to0.46.0. Preserve both Unreleased entries. Release acknowledgements remain in their independent per-user marker; card/menu order continues through existing UI preferences.

## Tried and ruled out
Do not merge an old green feature head when the latest main creates a changelog conflict or consumes the same minor version. Do not rerun an external advisory review for byte-identical reorder helper and smoke files; independently review the combined startup, constructor and harness integration and run checks on the resulting head.

## Watch out
MainWindow snapshot construction must suppress release dialogs so disconnected reorder validation remains deterministic. OnRootLoaded must continue completing the release-note root task and registering reorder sources. Preserve unrelated local AGENTS.md edits and landing-checkout notes during final cleanup.
