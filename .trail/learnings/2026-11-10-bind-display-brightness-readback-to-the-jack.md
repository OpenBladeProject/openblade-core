---
title: Bind display brightness readback to the written WMI instance
author: Jack
date: 2026-11-10
branch: fix/display-brightness-confirmation
touches:
  - CHANGELOG.md
  - src/OpenBlade.Display/AssemblyInfo.cs
  - src/OpenBlade.Display/WindowsDisplayBrightness.cs
  - src/OpenBlade.Display/WindowsDisplaySettingsController.cs
  - src/OpenBlade.SessionAgent/FunctionRowActionExecutor.cs
  - src/OpenBlade.SessionAgent/WmiDisplayBrightnessController.cs
  - tests/OpenBlade.Display.Tests/WindowsDisplayBrightnessTests.cs
  - tests/OpenBlade.SessionAgent.Tests/FunctionRowActionExecutorTests.cs
---

## Decided
Claude Opus 5.5/high reviewed the initial brightness fix at 593dbad3. Use the shared WMI acknowledgement policy in both settings/profile brightness and the WMI function-row fallback. Match the exact WMI instance and requested level on independent readback. A failed fallback press suppresses further held repeats and publishes no OSD value. Capture brightness and its identity from the same settings snapshot. The changelog links PR 939.

## Tried and ruled out
A Boolean return type establishes neither true nor false as a documented WMI success code. Preserve documented numeric error statuses and reject missing or unexpected response types. Do not add a readback delay without a reproduced stale reading, or strip unrelated fields from the full display snapshot. Trail check's historical version and brightness vocabulary matches do not change the applicable version bump, write confirmation, or null handling.

## Watch out
The review was read-only and the local WMI query only inspected the schema. The production hardware timing is unverified. Keep the native HID hotkey path separate and never replay a WMI setter after a missing or mismatched confirmation.
