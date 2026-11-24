---
title: "Driver function-row foreground pause never rearmed: Paused status rejected by store"
author: Jack
date: 2026-11-20
branch: fix/function-row-foreground-pause-status
cost: { minutes: 15, tokens: 186327 }
touches:
  - src/OpenBlade.SessionAgent/SnapTapDriverLoop.cs
  - src/OpenBlade.Tray.Core/SnapTapRuntimeStatus.cs
  - tests/OpenBlade.SessionAgent.Tests/KeyboardDriverStatusStoreTests.cs
---

## Decided
SnapTapStatusStore.IsValid (added 2026-10-24, d1f2506e) accepted a Failure only on terminal states. The foreground-pause rearm (2026-10-26, e2c1365d) publishes Paused + ForegroundUnavailable and the supervisor requires that exact on-disk status before rearming. Write threw ArgumentException inside the publisher's writer task (uncaught there, swallowed at Dispose), so every idle focus loss on the Razer-driver keyboard backend became UnexpectedExit(8) -> RecoveryRequired. The ownership journal is then kept until a later boot, so function-row media keys stay dead until Windows restarts. Fix: Paused may carry a Failure; Active and other non-terminal states still reject one.

## Tried and ruled out
Publisher flush timeout: the supervisor's 1500 ms heartbeat would have fired first and reported HeartbeatTimeout, not UnexpectedExit. Coordinator overwriting the joint status: it writes only on child exit or launch. Loosening IsCleanForegroundPause or trusting exit 8 alone: the 10-26 learning requires both the exit code and the matching status, so keep that.

## Watch out
The original ForegroundPause test built the status in memory and never round-tripped the store. KeyboardDriverStatusStoreTests now writes and reads the worker's exact failure (SnapTapDriverLoop.ForegroundUnavailableFailure). The driver worker has no persistent log, so its status-write failures are invisible in diagnostics. The same report also showed a separate boot-time HeartbeatTimeout (MayHaveSubmitted=True) that latches the coordinator until a preference toggle; that one is not fixed.
