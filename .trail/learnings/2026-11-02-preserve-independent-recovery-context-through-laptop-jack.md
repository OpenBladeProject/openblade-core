---
title: Preserve independent recovery context through laptop observations
author: Jack
date: 2026-11-02
branch: fix/codebase-reliability-audit
touches:
  - build/benchmarks/TelemetryProjection/README.md
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Tray.Core/MainViewModel.Telemetry.cs
  - src/OpenBlade.Tray.Core/TelemetrySnapshotProjector.cs
  - src/OpenBlade.Tray/App.PerformanceHarness.cs
  - src/OpenBlade.Tray/MainWindow.Telemetry.cs
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - tests/OpenBlade.Tray.Tests/TelemetryHistoryTests.cs
  - tests/OpenBlade.Tray.Tests/TelemetryMainViewModelTests.cs
  - tests/OpenBlade.Tray.Tests/TrayXamlStartupSmokeTests.cs
---

## Decided

A laptop-control recovery observation must remain read-only while preserving another recovery owner's HyperBoost restore intent until ordinary journal reconciliation. Publishing a confirmed inactive sample alone must not retire that context. Confirmed external HyperBoost still invalidates old exact fan authority immediately; this is read-only authority invalidation, not an owned restoration write.

Keep a newly observed power-source destination unconsumed in Automation while laptop-control recovery is pending. A later ordinary observation then detects and applies the destination with fresh admission, without replaying the uncertain laptop control. Explicit controls should return the specific pending-recovery diagnostic.

## Tried and ruled out

The review claim that recovery publication erased pending subsystem write blocks was disproven by the central PublishStatusUpdate aggregation and a focused regression. Do not add a second universal blocking mechanism. Current validated and experimental factories already share the exact model|VID:PID identity shape; hypothetical future migration is not a reason to relax identity matching.

## Watch out

Do not suppress the external-HyperBoost fan-authority epoch change while preserving owned restore provenance: publishing the first active sample would otherwise prevent the next observation from noticing that interval. Read-only recovery and later ordinary reconciliation are separate operations. Unknown write-only or corrupt-journal state still cannot be erased or inferred from process restart.
