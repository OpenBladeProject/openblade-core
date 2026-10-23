---
title: Rest scroll indicators before native snapshot frames
author: Jack
date: 2026-10-24
branch: feat/telemetry-analysis-typography
touches:
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - tests/Build.Tests/LocalizationTranslatorWorkflow.Tests.ps1
---

## Decided
CI run 39999409159 retained a real same-process translator ZIP mismatch. Only main-telemetry.png and its manifest entry changed: 624 opaque pixels in a two-column, 312-row right-edge strip were the fading vertical ScrollViewer indicator. Before every native render frame, the shared disconnected harness recursively selects NoIndicator without transitions for realized ScrollViewers and fails concretely if their template lacks that state. Production scrolling, Auto visibility, offsets, pixels and exact archive comparisons remain intact. The pinned WinUI 2.2.1 ScrollViewer template defines ScrollingIndicatorStates/NoIndicator and delayed IndicatorMode=None transitions; GoToState with useTransitions false is its direct supported path.

## Tried and ruled out
Two equal frames 100ms apart can stabilize either before or after the delayed indicator fade. Setting child IndicatorMode alone can lose to active storyboard precedence. Hiding scrollbars can change measurement, and masking opaque image detail would conceal meaningful differences.

## Watch out
Run the full unchanged translator workflow through both same-process and fresh-process archive comparisons, including native Tray and installer renders. An earlier capture had separate opaque chart-antialias variance; do not assume the scrollbar explains every historical mismatch or broaden pixel normalization without current evidence. Retain failure artifacts and inspect actual changed entries and pixels.
