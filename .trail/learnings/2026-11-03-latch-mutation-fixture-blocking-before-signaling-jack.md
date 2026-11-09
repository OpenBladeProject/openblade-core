---
title: Latch mutation fixture blocking before signaling readiness
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
FakeServiceClient.MutateAsync must snapshot BlockMutations before signaling MutationStarted. A readiness signal promises the current request has already latched its blocking behavior; later fixture changes apply only to subsequent requests.

## Tried and ruled out
The full Tray suite exposed intermittent failure of LightingHold_ClearOverridesDuringApplyOnlySupersedesLightingClear. The reset unblocked subsequent requests immediately after readiness; the original apply could then read the changed flag and bypass the intended gate. Production draft-generation ordering already handled the intended response order. Fix the shared fake rather than changing production lighting semantics. The existing theory now asserts the original apply remains incomplete after the reset response, before explicitly releasing it.

## Watch out
Do not add sleeps or broaden retries to make asynchronous fixture ordering appear deterministic. Readiness must follow all state decisions the waiting test relies on. This correction changes no hardware, lighting contract or runtime behavior.
