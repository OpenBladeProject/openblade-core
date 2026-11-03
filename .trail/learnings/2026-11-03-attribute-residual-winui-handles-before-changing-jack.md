---
title: Attribute residual WinUI handles before changing ownership
author: Jack
date: 2026-11-03
branch: fix/codebase-reliability-audit
touches:
  - src/OpenBlade.Tray/App.ResourcePerformanceHarness.cs
  - src/OpenBlade.Tray/App.ResourceRetirementDiagnostics.cs
  - build/benchmarks/NativeResources/Program.cs
  - build/benchmarks/NativeResources/RESULTS.md
---

## Decided
Residual Composition and example Event handles survive closure and a bounded retirement interval in pinned app-local WinUI. Exact native breakpoints matched one SystemVisualTarget duplicated Composition handle and two input-island Event handles to final types with one OPEN and zero CLOSE records. This identifies creation paths, not the final retaining reference. Keep production ownership unchanged; use the opt-in --observe-retirement probe to separate per-window counts, last-GC heap data and process private bytes.

## Tried and ruled out
Four extra GC/finalizer/dispatcher-drain rounds plus ten seconds idle did not release Composition or Event growth in matched pinned empty/dashboard controls. Private bytes did fall. An isolated Windows App SDK 2.6.5 build still grew Composition 14 to 25 over eleven activity windows; it is not a demonstrated fix. The repository pins remain Runtime 2.2.0 / WinUI 2.2.1. Read-only island enumeration showed only the main island, but absence from that list does not prove native destruction. The retained diagnostic omits enumeration and all temporary debugger hooks.

## Watch out
Native handle values are reusable: reject matches with intervening CLOSE/reuse. Capture syscall output pointers at entry and dereference after successful return on the same thread; volatile argument registers can be clobbered. Both self htrace and CDB htrace returned operation records with empty stacks, so use independently verified native breakpoints rather than inferring a privilege cause. Do not claim the two Event examples have a common window or kernel-object identity. Do not close borrowed framework islands, visuals, raw handles or the shared compositor. Private commit is not native heap size. Per-close counts precede collection; compare matching boundaries and observation flags. Evidence: build/benchmarks/NativeResources/RESULTS.md and results/2026-11-04-native-retirement-attribution.json. Framework lifetime remains unresolved; ordinary tray toggles reuse windows, while language rebuilds replace them.
