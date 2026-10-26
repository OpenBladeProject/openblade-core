---
title: Stable UTC previews and native flyout edge captures
author: Jack
date: 2026-10-23
branch: feat/telemetry-analysis-typography
touches:
  - build/Capture-LocalizationSnapshots.ps1
  - build/Export-LocalizationTranslatorPackage.ps1
  - docs/localization.md
  - src/OpenBlade.Tray/BatteryTimeline.cs
  - src/OpenBlade.Tray/LocalizationSnapshotPixelCanonicalizer.cs
  - src/OpenBlade.Tray/MainWindow.Battery.cs
  - src/OpenBlade.Tray/MainWindow.Telemetry.cs
  - src/OpenBlade.Tray/TelemetryChart.Analysis.cs
  - src/OpenBlade.Tray/TelemetryChart.Range.cs
  - src/OpenBlade.Tray/TelemetryChart.Snapshots.cs
---

## Decided
Disconnected localization captures format displayed timestamps in UTC through PresentationTimestamp; normal application presentation still uses Windows local time. Record the UTC policy in the snapshot render profile and require it during translator export. Telemetry and battery timeline/report labels share this formatting path.

Native flyout captures keep the real FlyoutPresenter and its rounded, partly transparent corners. The shared v3 pixel canonicalizer repairs one-step alpha rasterization drift only within a two-pixel image perimeter, retaining alpha 0/255, interior alpha and premultiplied BGRA bounds. WinUI and installer visual tests link the same implementation. Keep real image/frame and archive equality checks.

## Tried and ruled out
A machine timezone change between exports altered timestamp text despite fixed UTC fixture instants. Pin preview presentation rather than changing the Windows timezone or changing production time display. Fresh-process exports also differed in eight flyout corner pixels: RGB-only quantization left alpha variance intact. More layout waiting and skipping a screenshot do not fix that source of nondeterminism.

## Watch out
Version the canonicalization profile consistently in capture, export and their contract tests when its byte semantics change. Preserve retained reference/actual archives for diagnosis and run focused pixel/time tests before the full repeated-export suite. Run builds and native captures sequentially when they share the executable output path. Unit tests do not replace real Tray and installer renders or fresh-process package comparisons.
