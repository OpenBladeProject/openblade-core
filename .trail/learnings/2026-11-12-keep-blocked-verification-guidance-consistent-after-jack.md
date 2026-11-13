---
title: Keep blocked verification guidance consistent after review
author: Jack
date: 2026-11-12
branch: fix/950-startup-controller-conflict
touches:
  - Directory.Build.props
  - src/OpenBlade.Tray.Core/MainViewModel.cs
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - tests/OpenBlade.Tray.Tests/MainViewModelControllerAvailabilityTests.cs
---

## Decided
Claude review of issue 950 confirmed the lighting unavailable notice must share the same conditional read-failure guidance as the primary blocked-verification status. Treat nonblank controller-detection warnings like controller advisories for neutral unverified/unavailable presentation. Use DeviceCapabilities.Unavailable in the disconnected controller-blocked snapshot, matching the supplied startup diagnostics. Keep notification and inactive-session guidance regression coverage.

## Tried and ruled out
Do not replace current snapshot checks with the cached HasControllerAdvisory field: ApplyConnectionPresentation formats status before updating that field. Neutral Controls unavailable does not establish conflict causation or laptop presence, so this presentation correction does not require a new IPC field. General readiness and session-specific access are separate existing presentations; preserve the session-specific next action. HyperBoost is absent when initial capabilities are unavailable.

## Watch out
Share the localized long guidance through one property so hardware and lighting notices stay aligned. Clearing a detection warning or controller list does not establish successful device verification. Review suggestions are advisory: reproduce reachability before adding contract or session-precedence changes.
