---
title: Clarify the installer visual-test desktop boundary
author: Jack
date: 2026-10-18
branch: fix/hidden-visual-tests
touches:
  - ".editorconfig"
  - build/Capture-LocalizationSnapshots.ps1
  - build/CiImpactManifest.json
  - build/installer/OpenBlade.Installer.UI.Tests/InstallerWindowTests.cs
  - build/installer/OpenBlade.Installer.UI.Tests/OpenBlade.Installer.UI.Tests.csproj
  - docs/localization.md
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/TrayDashboardWindow.xaml.cs
  - tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj
  - tests/OpenBlade.Tray.Tests/TrayXamlStartupSmokeTests.cs
---

## Decided
The visual-test learning prohibits assigning WPF worker threads to a private desktop. Ordinary installer WPF STA threads stay on their original desktop and use verified compositor cloaking. Their existence, the appDomain runner setting, and documentation of logical-focus checks are compatible with the learning.

## Tried and ruled out
No additional implementation experiments were needed for this clarification.

## Watch out
Trail check's lexical conflict detector flagged the installer csproj comment and localization documentation because they mention WPF STA and desktop focus. These are false positives: neither file binds an installer thread to a private desktop. Keep the cloaking implementation and appDomain setting, with this explanation recorded in the PR.
