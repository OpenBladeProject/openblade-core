---
title: Driver keyboard delivery check pumps follow the observer catch-up
author: Jack
date: 2026-11-24
branch: fix/driver-snaptap-delivery-check
touches:
  - docs/keyboard-input-backends.md
  - src/OpenBlade.SessionAgent/SnapTapDeliveryTracker.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverLoop.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverSupervisor.cs
  - src/OpenBlade.SessionAgent/WindowsReactiveKeyboardSource.cs
  - tests/OpenBlade.SessionAgent.Tests/SnapTapDriverSupervisorTests.cs
  - tests/OpenBlade.SessionAgent.Tests/SnapTapKeyboardFeedTests.cs
---

## Decided
- In shared-feed mode (function row in the worker), a feed drain counts as a delivery-check pump only after the observer thread reports it caught up with its own Raw Input queue (GetQueueStatus QS_RAWINPUT clear after DispatchMessage) and the drain emptied the feed. This supersedes the earlier note that the pump guard covers only the worker thread.
- A worker pass more than 500 ms after the previous one rebases pending keys, so a frozen process cannot report a loss.
- The supervisor RecoveryRequired failure carries the worker exit code and the worker's own Reason/Stage/Context (SnapTapDriverSupervisor.RecoveryRequiredFailure). Read WorkerReason to tell OutputNotDelivered (delivery check), OutputCountMismatch (output refused after a heartbeat stop) and ForegroundChanged apart.

## Watch out
- The check only runs on worker passes, which input drives; a key lost just before typing stops is caught at the next key press.
- A catch-up callback failure means no pumps, so the check fails open, never closed.
- LimitDetails rebuilds every node with `with`, so Assert.Same on Cause fails; the context limit is 8192 characters.
