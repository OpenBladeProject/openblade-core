---
title: Keep real telemetry readings when gaps fill the point budget
author: Jack
date: 2026-11-02
branch: fix/codebase-reliability-audit
touches:
  - CHANGELOG.md
  - src/OpenBlade.Protocol/BatteryValidationFactory.cs
  - src/OpenBlade.Protocol/BladeWriteException.cs
  - src/OpenBlade.Protocol/ExperimentalBladeFactory.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Animation.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - src/OpenBlade.Protocol/IProductionLightingPort.cs
  - src/OpenBlade.Protocol/Pid02E0MatrixReport.cs
  - src/OpenBlade.Protocol/ProductionBatteryFactory.cs
---

## Decided
When required gap indices saturate a chart's point budget, select representative real readings first and insert an actual missing point between disconnected selected readings. Preserve first/last valid readings when the budget permits. With two points, prefer one real reading plus a gap over a fabricated line joining disconnected samples; two endpoints of one continuous run are safe. All-missing data stays missing.

Stop collecting required indices once saturation is certain, keeping scratch index lists bounded by the output budget. Preserve the ordinary non-saturated selection path, exact raw samples, statistics and events. Check both histories and narrowed viewports.

## Tried and ruled out
The previous evenly selected required-index fallback could spend every point on nulls and make a half-populated metric look empty. Equality saturation can fail too, not just counts greater than the budget. Preserving the old serialized output was useful for the first allocation refactor, but would preserve this correctness defect.

## Watch out
The final benchmark's full-day/full-viewport digest intentionally changes: it now retains 300 real and 300 missing points per metric. The other three viewport digests still match the original baseline. Report that distinction rather than claiming all four final outputs are identical. Do not remove real gaps, change raw statistics or infer hardware readings to make a chart look continuous. Timing and pool retention vary with concurrent validation; use semantic tests and measured allocations without fixed CI timing thresholds.
