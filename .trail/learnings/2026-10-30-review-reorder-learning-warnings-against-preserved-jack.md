---
title: Review reorder learning warnings against preserved contracts
author: Jack
date: 2026-10-30
branch: fix/section-drag-preview
touches:
  - Directory.Build.props
  - src/OpenBlade.Tray.Core/PageSections.cs
  - src/OpenBlade.Tray.Core/UiPreferenceStore.cs
  - src/OpenBlade.Tray/App.LiveReorderSmoke.cs
  - src/OpenBlade.Tray/LiveReorderBehavior.cs
  - src/OpenBlade.Tray/MainWindow.LiveReorderSmoke.cs
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - tests/OpenBlade.Tray.Tests/CapabilityPresentationTests.cs
  - tests/OpenBlade.Tray.Tests/PageLayoutPreferenceTests.cs
---

## Decided
The section/card reorder change advances current main 0.46.0 to 0.47.0 because it adds separately saved ordering inside settings sections. Retain pre-mutation EndDrag, explicit pointer-capture handoff, native drag thresholds, and ContentControl hardware gates. Trail check's current lexical matches against those preserved contracts are not semantic regressions; record that review in the pull request.

## Tried and ruled out
The historical 0.45-to-0.46 bump concerned a different merge base. Its value is not a fixed future version. The preview uses an existing XAML visual as a source and a separate composition overlay, so notes against rebuilding controls do not prohibit it. UiPreferences restore suppression is limited to card-order restoration and does not disable later deliberate preference saves.

## Watch out
Assess each reported learning conflict against the actual diff. A successful source review does not replace the disconnected render, boundary/persistence checks, or native input evidence. The latter remains incomplete when the user stops Computer Use.
