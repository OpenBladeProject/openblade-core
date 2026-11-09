---
title: Retain intermediate localization smoke diagnostics
author: Codex
date: 2026-11-07
branch: feat/battery-saver-low-fan-validation
touches:
  - tests/OpenBlade.Tray.Tests/TrayXamlStartupSmokeTests.cs
  - src/OpenBlade.Tray/App.ResourceLocalizationHarness.cs
---

## Decided
Include existing .ready, .phase, .phases.txt and .retention.json progress in timeout diagnostics before smoke cleanup deletes the sidecars. Preserve successful JSON reports and the original process deadlines. Exception/TRX output retains the bounded progress without changing production behavior.

## Tried and ruled out
An absent terminal report cannot identify a startup stall. The resource localization audit writes that report only after its workload and disposal, so the same absence can cover initial layout, language replacement, retirement observation or cleanup.

## Watch out
A last recorded phase establishes observed progress, not the reason for a timeout. Do not attribute these failures to system load or patch native-shell behavior without further evidence. Keep targeted repros disconnected and retain the existing timeouts.
