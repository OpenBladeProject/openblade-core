---
title: Isolate native growth with matched offscreen desktop controls
author: Jack
date: 2026-11-03
branch: fix/codebase-reliability-audit
touches:
  - build/benchmarks/NativeResources/Program.cs
  - build/benchmarks/NativeResources/README.md
  - build/benchmarks/NativeResources/RESULTS.md
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - src/OpenBlade.Tray/App.ResourcePerformanceHarness.cs
  - tests/OpenBlade.Tray.Tests/MainViewModelLightingEditorTests.cs
  - tests/OpenBlade.Tray.Tests/MainViewModelTests.cs
  - tests/OpenBlade.Tray.Tests/TrayXamlStartupSmokeTests.cs
  - build/benchmarks/NativeResources/results/2026-11-03-offscreen-desktop-comparison.json
  - src/OpenBlade.Tray/App.ResourceWindowPresentation.cs
---

## Decided
Use matched offscreen, nonactivating developer windows to separate private-desktop effects from native resource growth. Both modes initialize generated bindings explicitly, use AppWindow.Show(false), verify the complete virtual-screen exclusion, thread-local activation and process-wide foreground ownership, and keep the rejecting service client. The launcher supplies the exact owned desktop name; input mode requires fresh current/input desktop agreement and fails on lock or a secure-desktop switch. Earlier default-mode probes may omit the new optional report flags; offscreen mode still requires its metadata marker before launch.

## Tried and ruled out
The same hashed binaries in serial 30-second idle/activity runs reclaimed all tracked managed windows and content on both desktops. Handles increased 1508 to 1972 on the private desktop and 1312 to 1637 on the current input desktop after collection. This rules out private-desktop execution as a necessary condition for this offscreen growth; it does not identify application versus WinUI/composition ownership. A prior empty Window/Grid control grew much less, while clearing dashboard Content or adding layout dwell did not correct it. Keep those negative results; do not add production forced GC, COM release or delay.

## Watch out
Offscreen occlusion differs from visible presentation and ordinary tray toggles reuse a window. Timed activity includes fresh presentation admission; differing operation counts and host load prohibit growth-rate or CPU-speedup claims. The checked-in measurement predates later guard refinements and records actual binary hashes. Preserve primary plus cleanup failures and reject unknown foreground ownership after one retry of a confirmed HWND change. No installation, hardware mutation, service IPC or global input hook is part of these probes. Four Claude reviews plus independent Codex review informed the work; review is not execution evidence.
