---
title: Pad page editor follows the service request
author: Jack
date: 2026-11-21
branch: fix/cooling-pad-razer-driver-stack
cost: { minutes: 26, tokens: 141722 }
touches:
  - docs/cooling-pad.md
  - src/OpenBlade.Tray.Core/CoolingPadViewModel.cs
  - tests/OpenBlade.Tray.Tests/CoolingPadViewModelTests.cs
---

## Decided
CoolingPadViewModel records the service request and controlled state (Active or RecoveryRequired) that its editor shows. When a status brings a different request or controlled state, ApplyStatus reloads the editor with apply suppressed, so a request changed by another client, a service restart or an install shows on the page. A change waiting for its debounce (changePending) or an apply or release in flight (applying) always wins. A successful apply records the sent settings as the shown request, because the service stores the request exactly as sent. A refused or failed apply clears that record so the next status reloads the editor.

## Tried and ruled out
Loading the editor once, when the pad is first seen. A tray started after an install had released control showed "Set by the pad" while OpenBlade controlled the pad, and choosing Automatic sent the stale CPU source over the running Both. Comparing the editor with the snapshot through record equality alone: CoolingPadSettings holds curve lists, which compare by reference, so the curves need SequenceEqual.

## Watch out
The PadClient fake must echo the submitted settings in its readback, like the service. Without that, every automatic read reverts the editor to the fake's default request (fan Keep, shown as no mode) and later changes send nothing. When driving the installed tray with computer use, the Claude desktop app keeps an always-on-top window (class Chrome_WidgetWin_1) over the right edge of the screen that screenshots mask; clicks there land in Claude, not in OpenBlade. Move the OpenBlade window left first.
