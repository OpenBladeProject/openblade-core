---
title: Keep telemetry graph readouts bounded and command strips compact
author: Jack
date: 2026-10-23
branch: feat/telemetry-analysis-typography
touches:
  - CHANGELOG.md
  - build/Capture-LocalizationSnapshots.ps1
  - build/localization/TranslatorScreenshotMap.json
  - docs/telemetry-and-battery-history.md
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/App.xaml
  - src/OpenBlade.Tray/MainWindow.FeatureSnapshots.cs
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.Tray/TelemetryChart.Analysis.cs
  - src/OpenBlade.Tray/TelemetryChart.Range.cs
---

## Decided
Telemetry recording and sample commands use one native content-width wrap panel, with the existing shared command typography, focus behavior, 40-DIP minimum and source/tab order. Metrics and colors shares the sample-navigation strip. Full range sits beside the overview caption. Measure and arrange use the same eight-DIP gaps; arrange each row at its maximum height so captions center and wrapped commands remain aligned.

Each unit lane has a non-interactive, timestamped callout beside the synchronized crosshair. Values come from the exact selected raw sample and use the shared regular data typography and series colors. Clamp or flip the callout at graph edges and measure localized/scaled content before sizing the lane. Resolve its background and border through a real ThemeResource style, so local dark-mode overrides and high contrast stay authoritative. Repeated pointer events that map to the same sample skip presentation work unless follow/retention state needs changing.

## Tried and ruled out
Equal star columns stretch sparse actions across the page and waste rows. Fixed-width wrapping cells also waste space and break longer labels. A concrete brush read from Application.Resources can follow the application theme instead of the chart subtree. Native Button templates can retain CharacterEllipsis as their trimming configuration while showing the complete wrapped string; validate the matching realized TextBlock's IsTextTrimmed and bounds, not that inherited setting alone.

## Watch out
Use disconnected native renders and geometry/content assertions at normal/narrow widths, both graph edges, empty/expired history, production languages and pseudo-localization. Check keyboard pinning and range semantics independently of layout. Preserve the original selected state after fixture assertions. Keep custom callouts out of hit testing. Never use successful source/XAML checks as a substitute for real native captures, or replace Service/Session Agent binaries for this UI-only update. Nightly publication remains on hold.
