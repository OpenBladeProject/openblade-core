---
title: Explicit retry may release a same-boot driver ownership journal
author: Jack
date: 2026-11-22
branch: fix/driver-keyboard-deferred-subscription
cost: { minutes: 20, tokens: 137430 }
touches:
  - CHANGELOG.md
  - docs/snap-tap.md
  - src/OpenBlade.SessionAgent/InputHelperCommandLine.cs
  - src/OpenBlade.SessionAgent/Program.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverLoop.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverSupervisor.cs
  - src/OpenBlade.SessionAgent/SnapTapInputHost.cs
  - src/OpenBlade.SessionAgent/SnapTapInputProcess.cs
  - src/OpenBlade.SessionAgent/SnapTapProcessCoordinator.cs
  - src/OpenBlade.Tray/MainWindow.KeyboardBackends.cs
---

## Decided
The maintainer chose (2026-11-22) to let an explicit preference change release a same-boot driver ownership journal, reversing the Oct 30 "same-boot evidence remains closed" rule for that one path. The coordinator marks only the first launch after an Off generation cleared a latch (LaunchIsExplicitRetry, then the `explicit-retry` helper argument). The supervisor releases a same-boot journal only with that mark, an exactly equal boot identity, no OpenBlade purpose hook in fresh enumeration, and SnapTapDriverLoop.AllReleased true within 5 s; the exclusive helper mutex rules out a live owner. Automatic starts, later/unknown boots and Windows-hook journal readers stay unchanged. A retried owner whose stop is cleanup-confirmed clears its own coordinator recovery barriers.

## Tried and ruled out
A named event for the retry mark: mutable after launch and invisible in the process list; the shared argument grammar (also used by the deployment client) was extended instead. Automatic same-boot release on every start: rejected by the maintainer.

## Watch out
The accepted blind spot is a key an earlier worker sent as pressed that Windows does not report as down. Git Bash rewrites `subst W: /D` into a path; run it from PowerShell.
