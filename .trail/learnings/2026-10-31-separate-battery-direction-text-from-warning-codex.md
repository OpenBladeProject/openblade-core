---
title: Separate battery direction text from warning timing and correlate telemetry
author: Codex
date: 2026-10-31
branch: fix/battery-discharge-detection
touches:
  - src/OpenBlade.Tray.Core/MainViewModel.cs
  - src/OpenBlade.Service/WindowsPowerStateProvider.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
---

## Decided
Battery direction text changes directly with the signed battery rate; the external-power discharge confirmation timer gates the badge and warning episode, not Using versus Charging text. Diagnose a reported charging-label/capacity-drop disagreement by correlating system battery rate and remaining capacity, service GetStatus rate/source/access/revision, and per-battery charge/discharge telemetry during the same failing period.

## Tried and ruled out
Do not infer an installed observation incompatibility from different component version metadata alone; compare the exact relevant source commits. GetStatus is sufficient to inspect current cached service readings without invoking ObserveState, whose production path may reconcile hardware. Live-reading-only changes update service memory but are deliberately not broadcast as subscription pushes, so missing pushes alone do not prove stale sampling.

## Watch out
A normal USB-C charging capture does not explain an earlier falling-capacity case. Negative telemetry while unplugged validates only that condition. Preserve a bounded local recorder when the original workload is unavailable. Do not flip rate signs based on USB-C presence or one percentage decrement; capture signed rates, battery direction flags and capacity over time first.
