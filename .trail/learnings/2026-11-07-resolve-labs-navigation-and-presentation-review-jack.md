---
title: Resolve Labs navigation and presentation review findings
author: Jack
date: 2026-11-07
branch: feat/labs-sidebar
touches:
  - docs/developer-guide.md
  - src/OpenBlade.Tray.Core/MainViewModel.cs
  - src/OpenBlade.Tray.Core/Properties/Resources.qps-ploc.resx
  - src/OpenBlade.Tray.Core/UiPreferenceStore.cs
  - src/OpenBlade.Tray/MainWindow.FeatureSnapshots.cs
  - src/OpenBlade.Tray/MainWindow.GlobalSearchCatalog.cs
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - src/OpenBlade.Tray/Strings/qps-ploc/Resources.resw
  - tests/OpenBlade.Tray.Tests/MainViewModelTests.cs
---

## Decided
Route Labs guides through the shared search arrival navigator so stale callbacks, focus fallback and arrival cues share one implementation. A guide restores its hidden Keyboard section before opening the original Lighting control. Scope the experimental controls to the canonical Lighting template instance; dashboard copies keep them hidden. Search availability must follow the rendered controls, including disabled ones.

## Tried and ruled out
The separate navigate/scroll handler missed hidden sections and disabled-focus fallback. A shared Lighting template alone did not scope the importer away from the dashboard. Claude reviewed the isolated source snapshot, but did not read the literal diff; confirm every candidate against the actual branch changes before fixing it.

## Watch out
Use the sidebar's resource for the navigation-order label. Regenerate pseudo-localization with Update-LocalizationResources.ps1, because validation alone does not prove regeneration is byte-stable. Unknown navigation names should preserve other preferences; malformed tokens still use the existing rejection behavior.
