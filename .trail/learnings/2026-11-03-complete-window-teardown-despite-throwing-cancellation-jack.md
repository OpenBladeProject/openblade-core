---
title: Complete window teardown despite throwing cancellation callbacks
author: Jack
date: 2026-11-03
branch: fix/codebase-reliability-audit
touches:
  - CHANGELOG.md
  - build/benchmarks/NativeResources/Program.cs
  - build/benchmarks/NativeResources/README.md
  - build/benchmarks/NativeResources/RESULTS.md
  - src/OpenBlade.Tray/App.ResourceHandleDiagnostics.cs
  - src/OpenBlade.Tray/App.ResourcePerformanceHarness.cs
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - src/OpenBlade.Tray/TrayDashboardWindow.xaml.cs
  - tests/OpenBlade.Tray.Tests/MainViewModelTests.cs
  - tests/OpenBlade.Tray.Tests/TrayXamlStartupSmokeTests.cs
---

## Decided
MainWindow permanent disposal isolates synchronous cancellation and component disposal with the same trace-and-continue contract as its existing asynchronous cleanup helper. CancellationTokenSource.Cancel can throw AggregateException from registered callbacks; that must not skip initialization waiting, observation-loop shutdown, preference flushing or native title-bar release. Retain early native/logical Hide and the final native release guard.

## Tried and ruled out
A real throwing callback registered on the window feature lifetime reproduced the skipped-cleanup failure before the correction. The native fixture keeps that registration alive, verifies disposal still waits for pending initialization, faults initialization, then checks final native title-bar state and window reclamation. Do not infer a clean review from advisory findings: a claim that programmatic Close raises the cancelable Closing event was contradicted by the native observation. Pinned app-local runtime evidence does not establish behavior on future SDK upgrades.

## Watch out
Power-source state changes before its labels in one UI dispatcher turn. A fake test dispatcher can expose that intermediate state to another thread: waiting for ActivePowerSource alone caused a stale Battery profile label after USB-C selection. Use the existing complete-dispatch acknowledgment with distinct snapshot messages, keeping all outcome assertions, instead of sleeps or weakening expectations. Source-wiring checks should accept a disposable passed to the guarded helper; the real native negative controls prove execution and cleanup behavior.
