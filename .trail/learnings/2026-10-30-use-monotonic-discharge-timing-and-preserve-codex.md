---
title: Use monotonic discharge timing and preserve each warning occurrence
author: Codex
date: 2026-10-30
branch: feat/plugged-in-discharge-warning
touches:
  - src/OpenBlade.Tray.Core/ExternalPowerDischargeTracker.cs
  - src/OpenBlade.Tray.Core/NotificationPreferences.cs
  - src/OpenBlade.Tray.Core/MainViewModel.cs
  - src/OpenBlade.Tray/WindowNotificationCoordinator.cs
  - src/OpenBlade.Tray/WinFormsTrayIconHost.cs
---

## Decided
Confirm plugged-in discharge with monotonic TimeProvider timestamps, including sample-gap checks, so UTC corrections neither shorten nor restart the ten-second confirmation. Inject the same clock into the tracker and keep fake monotonic time independent of UTC in regressions.

Keep one stable discharge toast replacement tag, but include the monotonic episode number as its occurrence identity. The shared duplicate filter still suppresses repeat delivery of the same occurrence; a new confirmed episode can warn inside its ordinary thirty-second window without growing the tag dictionary. Preserve the existing three-argument ShouldEmit entry point for binary compatibility.

Notify hover label and overflow dependents whenever TrayIconState changes. Active HyperBoost on USB-C can have GenericError presentation; its admitted disable cleanup restores UsbCBalanced. Refresh native cached tooltip text after this badge-eligibility transition, not only after the earlier HyperBoostActive change.

## Tried and ruled out
Claude reviewed immutable range b31903b..ee0fd9da and returned nine candidates. Source and focused regressions established the three issues above. No measured noise establishes a battery-power deadband. Two samples spanning the selected ten-second cadence are valid under the existing gap policy; a new minimum sample count is not required. Status-only polling while access is inactive is intentional access recovery, covered by an existing regression. Keep the user-selected cadence and battery-only hidden reads. Duplicated expressions alone are not correctness findings.

## Watch out
Do not shorten the shared notification repeat window or vary the OS replacement tag to fix episode delivery. Never use retained subscription readings as new samples. Preserve known-recovery rearming, connection/error precedence, base mode/source artwork, Contracts/IPC and hardware admission. Reviewer output is a candidate list: reproduce each claim against the exact reviewed source and distinguish policy suggestions from observed defects.
