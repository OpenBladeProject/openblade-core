---
title: Recover idle SnapTap foreground loss only after verified guardian cleanup
author: Jack
date: 2026-10-26
branch: fix/snaptap-foreground-pause
touches:
  - Directory.Build.props
  - docs/snap-tap.md
  - src/OpenBlade.SessionAgent/SnapTapDriverLoop.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverSupervisor.cs
  - tests/OpenBlade.SessionAgent.Tests/SnapTapDriverInputProcessorTests.cs
  - tests/OpenBlade.SessionAgent.Tests/SnapTapDriverSupervisorTests.cs
---

## Decided
A null foreground window or missing keyboard layout while the SnapTap processor is idle is a recoverable focus observation, not a permanent input failure. Windows documents null foreground handles during activation changes. The owner report on nightly .103 reproduced ForegroundUnavailable three times after successful RZ09-0528 driver admission. The shared fix applies across the seven already reviewed driver profiles.

An unfaulted, physically released processor with no winner may close its subscription without reading another queued packet and return dedicated worker exit 8 with a fresh Paused diagnostic. Only the supervisor may rearm: prove exact child exit, independently remove canonical owned hooks with readback, preserve the original baseline/journal, dispose the child, and start a fresh worker. The new worker waits for valid foreground/layout and all keys released before subscribing. Keep the existing coordinator terminal latch and typed per-output admission.

## Tried and ruled out
Ignoring null focus and continuing to read would consume input without a safe target. Reopening the same subscription is unsupported, and Stop leaves the global hook records for guardian reconciliation. A Paused status alone is not retry authority. Require both dedicated exit and matching fresh worker metadata, no uncertainty or supervisor failure, and complete verified cleanup.

## Watch out
Winner zero alone does not prove idle: disabled processing tracks physical holds and Poison clears Winner. Held focus changes, ambiguous output, closure errors, heartbeat failure, foreign hooks and failed cleanup remain terminal. Persist the ownership journal between clean focus retries; a crash must not silently inherit the previous baseline. This is offline software validation, not physical SnapTap promotion for any model. The diagnostic screenshot initially showed an unavailable driver; after the owner reinstalled it, admission succeeded and exposed this separate foreground bug.
