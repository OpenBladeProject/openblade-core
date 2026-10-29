---
title: Refresh reorder review against main and honor formatter imports
author: Jack
date: 2026-10-29
branch: feat/live-layout-reorder
touches:
  - AGENTS.md
  - CHANGELOG.md
  - Directory.Build.props
  - build/identity/Assets/Square150x150Logo.png
  - build/identity/Assets/Square44x44Logo.png
  - build/identity/Assets/StoreLogo.png
  - src/OpenBlade.Tray/Assets/Brand/APP-ASSETS.md
  - src/OpenBlade.Tray/Assets/Brand/OpenBlade.Installer.ico
  - src/OpenBlade.Tray/Assets/Brand/OpenBlade.Mark.Dark.svg
  - src/OpenBlade.Tray/Assets/Brand/OpenBlade.Mark.Light.svg
---

## Decided
Before merging PR #861, integrate current main fc844714 (0.44.4), preserve both Unreleased feature and icon entries, and retain the required minor target 0.45.0. Update the PR version declaration to 0.44.4 -> 0.45.0. Preserve the unrelated AGENTS.md modification.

## Tried and ruled out
The first hosted quality failure was IMPORTS ordering in LiveReorderBehavior.cs, not a runtime test failure. dotnet build and the passing local tests did not enforce dotnet format's alias ordering; run the CI formatter instead of disabling the check. The corrected change only sorts ContentControl/Control, KeyEventHandler, Point and TextBox aliases.

## Watch out
Review Claude against the refreshed immutable head after integration. Reuse unchanged mouse and layout evidence, but require CI for the final head before merge. Historical version notes are provenance, not permanent pins. The merged main also carries its own HyperBoost presentation-completion test fix; do not attribute that unrelated fix to the reorder implementation.
