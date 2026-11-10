---
title: Confirm display brightness after Windows Boolean acknowledgement
author: Jack
date: 2026-11-10
touches:
  - src/OpenBlade.Display/WindowsDisplayBrightness.cs
  - src/OpenBlade.Display/WindowsDisplaySettingsController.cs
  - tests/OpenBlade.Display.Tests/WindowsDisplayBrightnessTests.cs
---

## Decided
The local RZ09-0581 Windows WMI schema declares a Boolean return for WmiSetBrightness, despite Microsoft Learn documenting a UInt32 status. Both Boolean values must reach independent brightness readback of the same internal panel before the shared Windows display controller reports success. Numeric status failures and unexpected types keep their diagnostics.

## Tried and ruled out
Both Boolean cases failed focused tests against the old UInt32-only validator. The correction belongs in the shared Windows display controller used by display sliders and profile application across all recognized Blade models. The separate brightness hotkey path does not use this validator.

## Watch out
The local check read the WMI schema only. Software tests do not establish the actual acknowledgement value or post-write readback timing on any panel. Preserve exact panel matching and never replay the setter after uncertain submission.
