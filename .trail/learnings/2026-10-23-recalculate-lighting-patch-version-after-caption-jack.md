---
title: Recalculate lighting patch version after caption merge
author: Jack
date: 2026-10-23
branch: fix/lighting-wake-flash
touches:
  - CHANGELOG.md
  - Directory.Build.props
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - tests/OpenBlade.Tray.Tests/TrayXamlStartupSmokeTests.cs
  - tests/OpenBlade.Tray.Tests/WinUiShellContractTests.cs
  - VERSIONING.md
---

## Decided
Before merging the dark-wake fix in PR #844, integrate current main and recalculate the patch version as 0.41.2 to 0.41.3 because caption-theme PR #843 already used 0.41.2. Preserve both Unreleased changelog entries. The lighting source and protocol tests remain byte-identical to the installed 8a4f34bc test revision.

## Tried and ruled out
Keeping the earlier 0.41.1 to 0.41.2 version declaration after main advanced would violate VERSIONING.md. Historical learnings document the version current at that earlier step, not a permanent version pin.

## Watch out
Trail keyword warnings about the earlier version and explanatory brightness-first comments do not establish a contract conflict. Preserve the implementation and admission/readback checks, document the semantic review, and validate the refreshed PR head. No release is authorized by this version increment.
