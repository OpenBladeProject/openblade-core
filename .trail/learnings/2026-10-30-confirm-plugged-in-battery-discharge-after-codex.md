---
title: Confirm plugged in battery discharge after ten seconds
author: Codex
date: 2026-10-30
branch: feat/plugged-in-discharge-warning
touches:
  - src/OpenBlade.Tray.Core/ExternalPowerDischargeTracker.cs
  - tests/OpenBlade.Tray.Tests/ExternalPowerDischargeTrackerTests.cs
  - tests/OpenBlade.Tray.Tests/MainViewModelExternalPowerDischargeTests.cs
  - CHANGELOG.md
---

## Decided
Confirm plugged-in battery discharge after ten seconds of accepted fresh negative battery-power observations. The user shortened the original thirty-second interval because it delayed the warning. This supersedes only the timing in the earlier sustained-discharge learning; badge composition, recovery rules, episode delivery, source verification and Live readings Off behavior stay unchanged.

## Tried and ruled out
Do not change the notification presenter's unrelated duplicate-suppression interval to implement detection timing. Duration belongs to ExternalPowerDischargeTracker. Keep explicit tests proving no entry before ten seconds and entry at ten seconds on AC and USB-C, followed by known recovery and rearming.

## Watch out
Unknown samples, gaps and clock rollback restart the candidate interval; they are not confirmed recovery for an active episode. Continue counting explicit battery observations rather than retained subscription data. Preserve the active mode and power-source glyphs under the additive badge.
