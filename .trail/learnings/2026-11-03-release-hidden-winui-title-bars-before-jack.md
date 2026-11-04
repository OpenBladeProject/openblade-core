---
title: Release hidden WinUI title bars before permanent native closure
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
  - tests/OpenBlade.Tray.Tests/TrayXamlStartupSmokeTests.cs
  - build/benchmarks/NativeResources/results/2026-11-04-titlebar-lifetime.json
---

## Decided
Release native title-bar state during permanent disposal of both WinUI window roots. Hide first; MainWindow additionally detaches its custom XAML title bar and disables extension before ResetToDefault. Dashboard uses a hidden native title bar without ExtendsContentIntoTitleBar. Preserve ordinary hide/show and keep generated-binding cleanup: native chrome cleanup and managed reclamation solve different retention paths. MainWindow hides logically and natively before its first disposal await, contains failed initialization, and releases chrome in finally. Dashboard finally releases chrome and closes while retaining the primary cleanup exception.

## Tried and ruled out
Matched private-desktop controls isolated hiding the native title bar as sufficient for GDI/Section/Dxgk growth in a plain Window/Grid. Removing dashboard content did not fix it; restoring hasTitleBar=true before Close did not fix it. Hide plus ResetToDefault sharply reduced it. Serial baseline/candidate/baseline evidence retained281/2/253 GDI objects under30-second phases; cycle totals differ, so this is not an exact per-window rate or CPU speedup. Keep literal diagnostic cleanup controls independent of the production helper. Do not add product GC, forced COM release, arbitrary delays, or native-memory CI thresholds.

## Watch out
The project carries app-local Windows App SDK Runtime2.2.0 and WinUI2.2.1 independently of shared .NET deployment. Native reset behavior must be revalidated on SDK updates; a managed catch cannot contain a native fail-fast. AppWindow.Closing is not a reliable observation point for programmatic Window.Close. The real native regression reads a seeded caption-color marker at AppWindow.Destroying and fails when dashboard cleanup is omitted. A pending, faulted initialization fixture also proves hide-before-await and final native cleanup. Map native type indices through each child report; GDI/native snapshots are after-warmup versus after-activity collection, while process counters have a separate activity-start boundary. Short-run residual Composition/other handles remain a separate uncertainty; do not claim flat resource use, installed-product power, or GPU residency from disconnected probes. See build/benchmarks/NativeResources/RESULTS.md for immutable report hashes and final validation.
