---
title: Give WinRT collection expressions concrete array types
author: Jack
date: 2026-11-18
branch: fix/pr-977-lighting-review
touches:
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/App.PerformanceHarness.cs
  - src/OpenBlade.Tray/GpuAppCleanupCoordinator.cs
  - src/OpenBlade.Tray/LocalizationSnapshotThxAudioAdapter.cs
  - src/OpenBlade.Tray/MainWindow.FeatureSnapshots.cs
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - src/OpenBlade.Tray/SpecialKeyBindingViewModel.cs
---

## Decided
PR 977 hosted solution build flagged CsWinRT1032 on 23 collection expressions across seven Tray files. Give each reported expression a concrete array type while preserving the public read-only contracts and collection contents.

## Tried and ruled out
The battery snapshot Trail match is lexical. Its interval data and admission are identical after the casts. Leave the analyzer enabled.

## Watch out
Check the ordinary Tray, developer harness, localization preview, and hosted solution build. A focused local build by itself does not prove that the hosted analyzer has accepted all three variants.
