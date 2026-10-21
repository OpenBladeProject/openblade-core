---
title: Gaming mode switches inherit compact state captions
author: Jack
date: 2026-10-21
branch: feat/touchpad-toggle
touches:
  - CHANGELOG.md
  - src/OpenBlade.Tray/MainWindow.xaml
  - tests/OpenBlade.Tray.Tests/CapabilityPresentationTests.cs
---

## Decided
Gaming mode switches in the shared KeyboardBehaviorRowsTemplate inherit empty state captions from CompactCardToggleSwitchStyle. A visible row label and the native toggle state already identify the control and its value.

## Tried and ruled out
Binding OnContent and OffContent to TrayDashboardGamingModeLabel overrides that style and adds the redundant On or Off caption beside the switch. The dedicated tray tile retains its separate status value.

## Watch out
Keep the accessible name and IsOn binding. Verify the actual rendered shared template after its build completes; an earlier compiled preview can still contain the old caption while a solution build is running.
