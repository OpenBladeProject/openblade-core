---
title: Keep Fn status separate from stroke admission and preserve saved host preferences
author: Jack
date: 2026-11-05
branch: feat/blade18-source-support
touches:
  - CHANGELOG.md
  - src/OpenBlade.SessionAgent/Blade18DefaultFnInputState.cs
  - src/OpenBlade.SessionAgent/FunctionRowDriverContext.cs
  - src/OpenBlade.SessionAgent/Rz090484FnInputState.cs
  - src/OpenBlade.SessionAgent/ServiceReactiveLightingLoop.cs
  - src/OpenBlade.SessionAgent/WindowsReactiveKeyboardSource.cs
  - src/OpenBlade.Tray.Core/ActiveProfileDraftState.cs
  - src/OpenBlade.Tray.Core/MainViewModel.cs
  - tests/OpenBlade.SessionAgent.Tests/Blade18DefaultFnInputStateTests.cs
  - tests/OpenBlade.SessionAgent.Tests/Blade18FunctionRowDriverTests.cs
---

## Decided

Report Blade 18 Fn observation availability from managed state. Revalidate the local session and exact raw-input collection per intercepted stroke. A failed native check can clear only the report it checked. An admitted Windows hook keeps default Fn shortcuts, with primary remapping off.

Laptop drafts must preserve saved host-managed Fn and Gaming Mode settings in both save paths, including after a lost reply or another writer. Rebase the affected draft and baseline field on accepted saves.

## Tried and ruled out

Polling native input admission on every idle iteration risks clearing good observations and repeats unnecessary work. An unmatched driver-owned F-row edge cannot trigger Windows hook recovery. Existing Windows press latches still drain across handoff.

## Watch out

Managed status is informational. Keep fresh native stroke admission and exact-model gates. Separate Fn reports and driver F-key edges still need physical ordering evidence before changing interpretation or timing. Keep hardware-backed laptop controls staged.
