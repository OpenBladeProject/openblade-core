---
title: Verify telemetry range and recording review findings
author: Jack
date: 2026-10-23
branch: feat/telemetry-analysis-typography
cost: { minutes: 1, tokens: 0 }
touches:
  - src/OpenBlade.Tray/TelemetryChart.cs
  - src/OpenBlade.Tray/TelemetryChart.Range.cs
  - src/OpenBlade.Tray/MainWindow.Telemetry.cs
  - src/OpenBlade.Tray.Core/TelemetryChartViewport.cs
---

## Decided
Review of PR #841 at ce13c1bf confirmed three telemetry interaction defects that remain unfixed at this revision. Range dragging and committing a time field set samplePinned, so hover inspection stops without an explicit sample pin. MainWindow passes the current recording metadata SessionId as the chart source identity, so Record/Stop resets the chart despite unchanged live history. Retention clamping moves a fixed window and chooses a replacement nearest sample without explaining that the pinned moment expired. Keep these as review findings, not claims that fixes have shipped.

## Tried and ruled out
Verified the first two through the exact PR diff and reachable UI-to-view-model paths. Invoking TelemetryTimeRangeSelection from the exact-head 0.42.0+ce13c1bf Core assembly moved a fixed 00:06-00:10 window to 00:08-00:12 after retention advanced to 00:08; the existing RetentionAdvanceAndSourceResetClampState test codifies that clamp. Counting state flags does not establish a correctness defect. Extra snapshot work, duplicate event refreshes and per-lane Draw callbacks are observable in source, but no measured lag or unbounded recursion was established.

## Watch out
Separate stopping range follow from pinning a sample. Source identity should distinguish live history from a replay document, rather than track whether live CSV recording is active. Bounded retention needs an explicit expired-selection behavior; silently showing another reading as pinned is misleading. Claude's read-only native reviewer inspected selected telemetry files rather than the literal diff or every changed file. Reproduce claims against the exact target and keep uncertain checkbox event ordering separate from confirmed findings. The review artifacts are outside the repo; this learning does not expand release, install or hardware authority.
