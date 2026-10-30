---
title: Complete sparse battery report coverage in shared presentation
author: Jack
date: 2026-10-30
branch: fix/battery-history-readability
touches:
  - CHANGELOG.md
  - README.md
  - README.ru-RU.md
  - README.zh-CN.md
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/BatteryTimeline.cs
  - src/OpenBlade.Tray/MainWindow.Battery.cs
  - src/OpenBlade.Tray/MainWindow.FeatureSnapshots.cs
  - src/OpenBlade.Tray/PresentationTimestamp.cs
  - tests/OpenBlade.Tray.Tests/PresentationTimestampTests.cs
---

## Decided
Treat a Windows battery report as potentially sparse: its typed constructor and IPC validate ordered positive intervals inside the window but do not require complete coverage. Complete missing prefix, internal and suffix periods in the shared Tray presentation, with Unknown source/activity and null measurements. Preserve supplied interval objects and keep the source report unchanged. The transport admits at most 512 intervals; derived presentation coverage is bounded to 1025 and must not be reconstructed as a transport report.

Update loading/error controls on each notification, then reuse the immutable confirmed report's rows and chart objects when its identity is unchanged. Remember the displayed report only after presentation succeeds. Include an interval's end date when the displayed start/end dates differ. Report snapshot routes explicitly request and assert sensor collection off. Keep README feature claims aligned in English, Chinese and Russian.

## Tried and ruled out
Claude's completed read-only review returned candidates, not verified defects. Its reconstruction from selected files missed existing manual captures and accessibility alternatives. Confirm each candidate against the exact diff, reachable code and typed contracts. Out-of-window and zero-width interval fixtures are not admitted reports. A generic tolerant preference converter and hundreds of focusable graph bars are separate designs; retained legacy enum identifiers and the native interval-details text remain intentional. Repeated redraws alone do not prove measured UI lag.

## Watch out
A fully covered fixture hides missing-window gaps. Test sparse edges, internal gaps, empty/zero windows and the maximum interval count, and render sparse details at narrow width. Assert rows/segments survive transient loading and failure updates. Preserve source/IPC/service/Session Agent contracts and hardware behavior; the fix belongs to presentation.
