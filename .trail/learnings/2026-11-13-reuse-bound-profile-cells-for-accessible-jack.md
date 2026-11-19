---
title: Reuse bound profile cells for accessible navigation names
author: Jack
date: 2026-11-13
branch: feat/profile-card-navigation
touches:
  - src/OpenBlade.Tray/MainWindow.ProfileNavigation.cs
  - src/OpenBlade.Tray/MainWindow.xaml
  - tests/OpenBlade.Tray.Tests/CapabilityPresentationTests.cs
---

## Decided
Build each profile row's accessible name from its existing ContentControl.Content values and localized title. Bind to the cells rather than reading MainViewModel profile presentation properties again. This keeps saved values, profile names and pending descriptions in the row announcement while reusing the already-formatted profile data.

## Tried and ruled out
Binding the automation name directly to all three view-model profile presentations repeats complete profile formatting for each row. Those properties build a full profile presentation, not a single field. A native row name only needs the already-bound cells. Keep the instance helpers required by the generated unqualified x:Bind calls.

## Watch out
Exercise live language replacement and offscreen window teardown after adding named controls or bindings. The final focused run passed all four profile UX tests and both native smokes; disconnected compact and comparison renders also passed navigation and accessibility checks. Do not infer a universal timing root cause from a single timeout or this optimization.
