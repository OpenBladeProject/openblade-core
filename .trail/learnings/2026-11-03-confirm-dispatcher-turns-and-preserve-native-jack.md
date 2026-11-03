---
title: Confirm dispatcher turns and preserve native evidence timestamps
author: Jack
date: 2026-11-03
branch: fix/codebase-reliability-audit
touches:
  - src/OpenBlade.Tray/App.ResourceRetirementDiagnostics.cs
  - src/OpenBlade.Tray/App.ResourcePerformanceHarness.cs
  - tests/OpenBlade.Tray.Tests/TrayXamlStartupSmokeTests.cs
  - build/benchmarks/NativeResources/RESULTS.md
---

## Decided
Residual native-resource retirement observations now await an acknowledged low-priority DispatcherQueue callback after each GC/finalizer round, bounded to five seconds, before recording the stage. This confirms one dispatcher turn; it does not flush native composition, establish an empty queue, or prove all future teardown finished. LastGc index, generation and concurrency describe the observed latest collection. Reuse warmup/activity native and GUI snapshots and disclose opt-in activity/report overhead in the child report.

## Tried and ruled out
The earlier investigation's stages named drain-N only awaited delays around GC. Treat them as dispatcher pumping opportunities, not confirmed low-priority callbacks. Preserve those historical measurements and label that limitation. Fixed retirement timing remains an experimental control; the offscreen smoke test explicitly budgets its extra twelve seconds. Bounded rooted report records deliberately contribute to managed readings and are not evidence of a managed window leak.

## Watch out
PowerShell ConvertFrom-Json/ConvertTo-Json can parse ISO timestamp strings and reserialize their offsets. A sanitizer claiming verbatim timestamps must preserve the raw strings: this evidence is exported with Python json parsing, and raw report hashes identify the immutable inputs. Offset rewriting did not change the represented instant, but violated the provenance claim. Do not reuse converted data and label it verbatim. These corrections implement Claude's review; they do not establish a production fix for WinUI's private visual-target retention. The earlier native attribution and ownership learning still applies.
