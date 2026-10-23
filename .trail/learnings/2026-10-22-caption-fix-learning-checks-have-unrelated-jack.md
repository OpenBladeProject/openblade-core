---
title: Caption fix learning checks have unrelated keyword matches
author: Jack
date: 2026-10-22
branch: fix/840-caption-theme
touches:
  - Directory.Build.props
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - tests/OpenBlade.Tray.Tests/TrayXamlStartupSmokeTests.cs
  - tests/OpenBlade.Tray.Tests/WinUiShellContractTests.cs
---

## Decided
For issue 840, the required patch version changes 0.41.1 to 0.41.2 without preparing or publishing a Nightly. The change only synchronizes native caption themes and adds a disconnected WinUI regression.

## Tried and ruled out
The trail check keyword matcher flags unrelated driver-catalog, THX binding, firmware-read, gaming-toggle caption and flyout-content learnings against the word caption, root, read, content, light or the version number. Those findings are lexical false positives: this patch changes none of those contracts, performs no driver operations, adds no caption foreground colors and makes no HDR pixel-validation claim.

## Watch out
Keep the Nightly publication hold intact. Reassess actual semantic conflicts instead of treating shared words as evidence of a regression. The draft PR records the reviewed trail check findings.
