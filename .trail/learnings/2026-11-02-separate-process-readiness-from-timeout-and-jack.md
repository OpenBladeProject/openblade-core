---
title: Separate process readiness from timeout and cancellation assertions
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

Native process tests establish the harmless child's ready-and-waiting phase before measuring the installer monitor's exit, timeout or cancellation behavior. Give the monitor an independent Process wrapper, release the named event in finally, then join the child and observe the monitor. A fixture watchdog bounds failure cleanup; only a test-owned stuck child may be killed there.

Cancellation tests whose fake runner ignores cancellation use entered and completion signals. Cancel explicitly after entry, assert the request cancels while the runner remains pending, then release and observe both tasks in finally.

## Tried and ruled out

Hosted CI exposed 30-second broker fixture waits and a launcher test that raced 10ms cancellation against a 250ms fake completion. Equal or relative wall-clock delays do not establish order under scheduler load. These failures did not prove a production monitor bug; production deadlines and behavior remain unchanged.

## Watch out

Keep the 100ms broker timeout, cancellation classification, exact binding failure and child-alive assertions. Do not pass by skipping real child lifetime checks or merely increasing production timeouts. Preserve the initial CI log and verify the corrected fixtures in the complete solution and hosted run.
