---
title: Use flat desktop telemetry tools and transient graph readouts
author: Jack
date: 2026-10-23
branch: feat/telemetry-analysis-typography
touches:
  - CHANGELOG.md
  - docs/telemetry-and-battery-history.md
  - docs/ui-typography.md
  - src/OpenBlade.Tray/AppTypography.cs
  - src/OpenBlade.Tray/ContentWrapPanel.cs
  - src/OpenBlade.Tray/MainWindow.FeatureSnapshots.cs
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.Tray/TelemetryChart.Range.cs
  - src/OpenBlade.Tray/TelemetryChart.Snapshots.cs
  - src/OpenBlade.Tray/TelemetryChart.cs
---

## Decided
The user found the telemetry controls oversized even after the contextual layout redesign. Use shared flat desktop toolbar roles with 32-DIP minimum targets and 16-DIP decorative glyphs while retaining the native body type scale, focus, hover, pressed and toggle states. Keep previous/next adjacent in one tool group with play/pause following and metric settings. Full range is a native inline text action beside the range caption. Page commands and forms retain their 40-DIP role.

Graph inspection has a pointer lifetime independent of selected data. Enter/move/press activates synchronized graph overlays; exit/cancel/capture loss/unload hides callouts, crosshairs and selected markers. A pinned sample and table values remain selected off the graph. Off-graph keyboard sample navigation updates and pins the table without opening graph overlays; returning to a graph restores its selected reading.

## Tried and ruled out
Content-width buttons alone, 40-DIP page-action styling, and arrows separated by a long timestamp still looked bulky. Do not shrink body text or fix heights to make the toolbar fit. Do not clear a selected sample, change its range or undo pinning merely to hide a tooltip. Do not let live redraws reopen off-graph details.

## Watch out
Use native normal/narrow and translated renders with complete-label and geometry assertions. Default/narrow snapshots deliberately show idle graphs, while the edge route deliberately shows hover readouts; snapshot state must be restored after assertions. Verify pinned exit, redraw, keyboard navigation and hover re-entry against real WinUI controls. The dark snapshot now shows the toolbar at the top. Keep service/Session Agent and IPC unchanged for UI-only deployment. The existing translator archive antialias reproducibility failure is not established as fixed by this change. No Nightly until the user requests it.
