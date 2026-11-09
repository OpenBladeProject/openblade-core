---
title: Classify direct layout reordering as a new capability
author: Jack
date: 2026-10-29
branch: feat/live-layout-reorder
touches:
  - Directory.Build.props
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - tests/OpenBlade.Tray.Tests/TrayXamlStartupSmokeTests.cs
  - src/OpenBlade.Tray/App.LiveReorderSmoke.cs
  - src/OpenBlade.Tray/LiveReorderBehavior.cs
  - src/OpenBlade.Tray/MainWindow.LiveReorderSmoke.cs
  - VERSIONING.md
---

## Decided
This feature advances main's 0.44.3 to 0.45.0: always-available direct reordering of live cards and navigation is a new user-facing capability, so VERSIONING.md requires a minor increment. The prior 0.44.2 to 0.44.3 presentation-only icon patch remains correct for that earlier change.

## Tried and ruled out
Do not reuse the earlier patch version decision merely because this feature also touches the Tray. Classify the behavior delivered by the current PR and recompute against current main.

## Watch out
Trail's historical version warning for the earlier icon patch is not a rule that all subsequent UI work must stay within 0.44. Explain this distinction in this feature's commit and PR version fields. IPC and preference schemas are unchanged.
