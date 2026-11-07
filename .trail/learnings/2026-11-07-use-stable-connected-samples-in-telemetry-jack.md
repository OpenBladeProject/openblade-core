---
title: use stable connected samples in telemetry translator snapshots
author: Jack
date: 2026-11-07
branch: feat/global-settings-search
touches:
  - src/OpenBlade.Tray/MainWindow.FeatureSnapshots.cs
  - OpenBlade.slnx
---

## Decided
The v5 slider fix passed hosted CI for PR #883, but an independent local repeated export retained a second mismatch: three pixels on a diagonal battery-power chart stroke in main-telemetry-metrics.png. Keep screenshot fixture segments horizontal at their original connected pairs (0/2 seconds and 20/22 seconds), while preserving level changes across gaps, missing readings, selection at 14 seconds, the latest 28-second reading, both power-flow signs and profile/power-source events. Apply this to the shared disconnected telemetry fixture because all chart routes and metric colors use the same native Polyline rasterizer. Production telemetry data, geometry, rendering and byte-exact archive checks remain unchanged.

## Tried and ruled out
Do not broaden pixel suppression over thin colored chart strokes or invent a second renderer for screenshots. A route-only battery workaround leaves the same native diagonal rasterization behavior in the other telemetry screenshots. Retained PNG differences identify the raster edge, not a semantic change in the plotted sample. Three fresh native captures of the adjusted main telemetry-metrics fixture had identical SHA-256 hashes.

## Watch out
Keep real gaps and signed battery flow visible; flatten connected sample pairs rather than every reading. Existing native analysis assertions must still run. The standalone installer snapshot project is outside OpenBlade.slnx and needs its own restore before running LocalizationTranslatorWorkflow.Tests.ps1, as the hosted workflow does. Continue treating ad hoc native flyout comparisons separately from the pinned package-render profile; do not erase thin outlines or selected-state detail to make them match.
