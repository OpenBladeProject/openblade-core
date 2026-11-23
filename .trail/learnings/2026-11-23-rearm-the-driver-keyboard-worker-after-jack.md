---
title: Rearm the driver keyboard worker after a clean heartbeat stall
author: Jack
date: 2026-11-23
branch: fix/driver-keyboard-stall-rearm
cost: { minutes: 11, tokens: 210622 }
touches:
  - Directory.Build.props
  - docs/snap-tap.md
  - src/OpenBlade.SessionAgent/SnapTapDriverLoop.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverSupervisor.cs
  - tests/OpenBlade.SessionAgent.Tests/SnapTapDriverSupervisorTests.cs
---

## Decided
The maintainer chose to rearm, not latch, after a driver keyboard input-loop HeartbeatTimeout when the stalled worker then honours the stop cleanly. This reverses the Nov 21 learning's ruled-out item. `SnapTapDriverSupervisor.IsCleanStall` requires that exact supervisor failure (`HeartbeatTimeoutFailure(started: true)`, record equality), then either exit 0 with a fresh Off status carrying the worker's own PID, or the dedicated clean-pause exit with its own fresh pause status. Hook reconciliation must also have passed. `TryAdmitStallRearm` allows three rearms per 10-minute sliding window; after that the old Failed latch applies. The worker's pre-hook stop now publishes Off so a stall in WaitingForRelease qualifies.

## Tried and ruled out
Raising the 1.5 s input budget, or adding a heartbeat timer thread. Accepting exit 1 when a keystroke was in hand and CanOutput refused after the stop: the processor poisons, so it still latches.

## Watch out
An independent Fable review found the gate fails closed. A lost or late final status write reads back as Paused or non-Off, so it latches. Residual paths that still latch: a stall with a keystroke in hand, a stop that lands before the new worker's start check (exit 1), and a kill after 6 s. Two stuck-key cases are plausible but unverified: an A/D key-down forwarded during the F-key drain whose release is still unread, and the rearmed worker hooking while A/D is physically held. Git Bash `sed -i` strips CRLF from repo files, so re-add `\r` afterwards.
