---
title: Separate telemetry freshness from chart projection
author: Jack
date: 2026-11-02
branch: fix/codebase-reliability-audit
touches:
  - CHANGELOG.md
  - src/OpenBlade.Protocol/BatteryValidationFactory.cs
  - src/OpenBlade.Protocol/ExperimentalBladeFactory.cs
  - src/OpenBlade.Protocol/ProductionBatteryFactory.cs
  - src/OpenBlade.Service/IVerifiedBladeDeviceProvider.cs
  - src/OpenBlade.Service/ServiceComposition.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/WindowsVerifiedBladeDeviceProvider.cs
  - src/OpenBlade.Tray.Core/TelemetryChartViewport.cs
  - src/OpenBlade.Tray.Core/TelemetryHistory.cs
---

## Decided
Keep telemetry history retention separate from pure snapshot projection. Project statistics in one pass and use pooled private value-type scratch points, materializing only the selected public points. Preserve exact gap/endpoint selection, signed values, raw samples and output ownership. Four deterministic live/replay full/restricted output hashes matched the baseline.

Route freshness notifications to the source label, recording/error notifications to commands, and accepted history to one chart rebuild. The existing observation completion still advances the live time range when no newer sample arrived; it must skip replay, newer-sample and disposed cases. Keep sampling cadence and hidden-read behavior unchanged.

## Tried and ruled out
A full-range snapshot cache was unsafe without a trusted immutable projection key: public records can be cloned with different point budgets and expose caller-owned lists. Mechanical MainViewModel splitting had no demonstrated independent ownership benefit. Do not claim replay-history benchmark time as live sensor latency or disconnected constructor time as installed startup.

## Watch out
The opt-in benchmark under build/benchmarks/TelemetryProjection measures actual Core methods and compares complete output digests. Pool retention affects allocation measurements, especially with explicit collections and concurrent validation; report measured conditions instead of hard CI timing limits. The developer-only native harness uses the private desktop fixture and no service observation. It asserts real rebuild counts and no-sample/replay/disposal behavior, captures PNG output, and finishes asynchronous artifact writes before closing the last XAML window.
