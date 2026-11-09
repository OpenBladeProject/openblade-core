---
title: Reserve chart readings and redraw once per displayed source change
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

Reserve at least half the chart budget for real readings before collapsed missing-point indices crowd them out; exact budget saturation is too late. Keep ordinary low-gap selection unchanged and preserve actual null separators between disconnected selected readings. Statistics remain based on the complete raw data.

Use TelemetryHistorySnapshot as the single chart notification for accepted live samples and replay source changes. Freshness refreshes source labels; replay/recording/error changes refresh commands. Live recording continues while replay is displayed, without notifying properties whose displayed values did not change. No-sample live completion still advances the time axis; newer samples, replay changes, and disposal own or cancel their respective presentation.

## Tried and ruled out

Only switching algorithms when required indices reached the whole budget still hid almost all readings just below saturation. Manual full refreshes in replay actions and their finally block duplicated property-change rendering. Suppressing all observation work during replay would break background recording, so only unchanged displayed-property notifications are suppressed.

## Watch out

Exercise real CSV open and close, failed and superseded loads through the native action handlers; synthetic property-change batches alone miss duplicate wrapper refreshes. Preserve command/error feedback and observation generations. The developer harness stays disconnected, writes artifacts before its last window closes, and cleans its temporary CSV on success or process-test cleanup. Benchmark hashes are output checks, not hardware admission or installed latency evidence.
