---
title: Warn on fresh sustained battery discharge while plugged in
author: Codex
date: 2026-10-30
branch: feat/plugged-in-discharge-warning
touches:
  - src/OpenBlade.Tray.Core/ExternalPowerDischargeTracker.cs src/OpenBlade.Tray.Core/MainViewModel.ExternalPowerDischarge.cs src/OpenBlade.Tray/WindowNotificationCoordinator.cs src/OpenBlade.Tray/MainWindow.xaml.cs
---

## Decided
Issue #865 adds a read-only tray warning for sustained battery discharge on either confirmed external source (PluggedIn or UsbC). Count only successful, accepted ObserveStateAsync responses that explicitly requested BatteryPower. Repeated service pushes, replay data, missing samples, long observation gaps, and clock rollback do not establish thirty seconds of discharge. Keep the active episode through unknown/unverified samples; only verified unplug or a fresh nonnegative battery reading confirms recovery.
Use one monotonically increasing episode counter for notification delivery. Prime the coordinator silently and consume each episode before checking preference or attempting delivery, so preference changes and failed presentation cannot replay it. The additive ExternalPowerDischarge notification preference is enabled by default and independent of profile/source-change notifications.
Reuse the existing active observation loop while the tray is hidden. Its hidden warning path requests BatteryPower only, avoiding GPU/temperature/fan polling. Live readings Off stops this observation. No new loop, timer, Contracts/IPC change, service backend or hardware-control command is needed.

## Tried and ruled out
Do not detect duration from retained LiveReadings in subscription snapshots or treat a status revision as a battery sensor timestamp. Do not rely on the existing hover-only one-shot loop: it is inactive without a hover and optional hover sensors may exclude battery power. Do not use the presenter's thirty-second duplicate suppression as an episode latch.
An unverified Battery classification must not rearm the episode. A stale warning tooltip must not override the generic connection/identity/error icon. The discharge warning takes precedence over valid performance/HyperBoost icons only; existing error precedence and enum values remain intact.

## Watch out
New native icons must be explicitly admitted by .gitignore as well as the loader and native-size asset test. Keep the SVG/generator with the two theme ICO files and inspect all five native sizes. Update semantic settings-row tests, regenerate English/pseudo resources, and review Chinese/Russian meanings together. This is a warning about measured battery contribution, not a diagnosis of charger failure or a CPU/GPU power-cap feature. Prior power-budget research notes are a separate workstream.
