---
title: Hold stale power-profile maintenance through read-only recovery
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
  - tests/OpenBlade.DeploymentBroker.Tests/WindowsBrokerInstallerProcessRunnerTests.cs
  - tests/OpenBlade.Tray.Tests/ElevatedServiceControlLauncherTests.cs
  - tests/OpenBlade.Tray.Tests/TelemetryHistoryTests.cs
---

## Decided

When read-only laptop recovery discovers a different known power source, hold the existing retained-lighting recovery callback until ordinary fresh destination processing begins. That same shared callback briefly defers ordinary Driver acquisition; the separate confirmed-low-power cleanup predicate is unchanged. Clear the hold even when the source reverted and no destination transition is needed.

Fan-curve maintenance requires agreement between Automation's source and the freshly published status source before temperature sampling or writes. Fresh hardware power matching status alone is insufficient while the destination profile remains unconsumed.

## Tried and ruled out

Source-independent Driver maintenance did not justify another cross-backend admission API. A blanket extra-observation block for every successful laptop recovery was unnecessary. Existing thermal candidates already bind destination and generation; the read-only recovery pass creates no new candidates.

## Watch out

Retain actual observed power in presentation. Do not disguise a deferred transition by reporting the previous adapter. Test both USB-C destination processing and source reversion, and keep uncertain laptop commands unreplayed.
