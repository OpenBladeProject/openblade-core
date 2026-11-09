---
title: Keep release acknowledgements independent of saved settings
author: Jack
date: 2026-10-29
branch: feat/whats-new
touches:
  - CHANGELOG.md
  - src/OpenBlade.Tray.Core/UiPreferenceStore.cs
  - src/OpenBlade.Tray.Core/WhatsNewRelease.cs
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/App.xaml.cs
  - src/OpenBlade.Tray/MainWindow.WhatsNew.cs
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - tests/OpenBlade.Tray.Tests/WhatsNewReleaseTests.cs
  - tests/OpenBlade.Tray.Tests/WhatsNewShellContractTests.cs
  - src/OpenBlade.Tray.Core/WhatsNewSeenStore.cs
---

## Decided
The final release acknowledgement is a separate, bounded per-user whats-new.version file with atomic replacement. Loading or saving that marker never reads or rewrites preferences.json. MainWindow loads marker state separately from ordinary settings and optional note failures leave update checking available. Lazy bundled notes use the release identity without commit metadata. The live-localization harness passes an explicit suppression flag.

## Tried and ruled out
Keeping the marker in UiPreferences made an automatic acknowledgement rewrite a complete settings snapshot. Blocking the shared preference save queue after a startup load failure then prevented deliberate settings changes and recovery for the session. Both approaches were removed. No general preference persistence behavior is changed by this feature.

## Watch out
This supersedes the earlier note's suggestion to use an optional UI preference for the marker. Verify independence by holding an invalid preferences.json open exclusively while saving the acknowledgement. Render dialog content through its popup root and reuse the established native localization harness.
