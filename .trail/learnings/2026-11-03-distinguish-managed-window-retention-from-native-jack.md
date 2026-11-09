---
title: Distinguish managed window retention from native performance evidence
author: Jack
date: 2026-11-03
branch: fix/codebase-reliability-audit
touches:
  - CHANGELOG.md
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/MainWindow.xaml.cs
  - src/OpenBlade.Tray/TrayDashboardWindow.xaml.cs
  - tests/OpenBlade.Tray.Tests/TrayXamlStartupSmokeTests.cs
  - build/benchmarks/BackgroundWorkloads/BackgroundWorkloads.csproj
  - build/benchmarks/BackgroundWorkloads/Program.cs
  - build/benchmarks/BackgroundWorkloads/README.md
  - build/benchmarks/BackgroundWorkloads/RESULTS.md
  - build/benchmarks/BackgroundWorkloads/results/2026-11-03-measurements.json
---

## Decided

Separate native Tray lifetime, offline background helpers, and installed hardware/energy evidence. Disconnected private-desktop validation uses rejecting IPC, isolated preferences, bounded history, exact sample redraw checks, owned processes and binary hashes. It does not establish installed idle power or physical GPU residency.

WinUI Window x:Bind leaves its Activated delegate subscribed after StopTracking. Baseline/candidate/restored-baseline runs retained 14/0/14 closed dashboard windows and content graphs. Permanent MainWindow and TrayDashboardWindow disposal now shares exact-signature delegate detachment followed by StopTracking. Log failures independently so remaining teardown proceeds. Native regression checks weak references, AppWindow destruction events and reveal-host state; pin dashboard/dwell/rendering settings against inherited environment overrides.

## Tried and ruled out

StopTracking alone leaves the activation delegate. Referencing generated bindings directly fails XAML's first compile pass; exact public-signature reflection works with this generator. Clearing Content before Close did not correct remaining native growth and was removed. View-model subscriptions decreased after collection, so an accumulating listener list was not established. A 250 ms dwell and explicit XAML rendering callbacks did not remove native growth. Rendering subscription changes frame demand and is opt-in only.

## Watch out

Managed lifetime is corrected, but private-desktop native growth remains unresolved: the five-minute activity candidate reclaimed all 107 dashboards yet handles rose 1442 to 5635. Diagnostic growth was mostly Section and DxgkCompositionObject. Product versus WinUI/composition causation is unproven. Do not add forced COM release, product GC, arbitrary delays or dependency upgrades on this evidence. Current-process internal-API diagnostics bound counts/buffers/retries, validate lengths and return unavailable rather than guess. Callbacks are not a physical-present fence.

Loaded-layout timing is a polled upper bound, not cold startup or first paint. Hidden-page chart work is intentional stress. Forced collections occur outside timed phases. Clock regression invalidates sample acceptance. Offline StatusSubscriptionHub and actual Platform sampling-helper benchmarks use fake inputs and source hashes; 27 passing phases do not measure complete Service/Session Agent performance. Preserve numerical evidence and unresolved gaps in benchmark RESULTS documents. Claude reviews informed cleanup, phase-file sharing, sample counts and bounded subprocess drains; reviews do not replace execution evidence.
