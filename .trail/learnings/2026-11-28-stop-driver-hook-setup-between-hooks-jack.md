---
title: Stop driver hook setup between hooks
author: Jack
date: 2026-11-28
branch: fix/driver-keyboard-orphaned-hooks
cost: { minutes: 27, tokens: 137856 }
touches:
  - docs/snap-tap.md
  - src/OpenBlade.SessionAgent/RazerSnapTapDriverTransport.cs
  - src/OpenBlade.SessionAgent/RazerSnapTapHookSession.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverLoop.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverSupervisor.cs
  - src/OpenBlade.SessionAgent/SnapTapWorkerProgress.cs
  - tests/OpenBlade.SessionAgent.Tests/FunctionRowDriverTests.cs
  - tests/OpenBlade.SessionAgent.Tests/RazerSnapTapHookSessionTests.cs
  - tests/OpenBlade.SessionAgent.Tests/SnapTapDriverSupervisorTests.cs
  - tests/OpenBlade.SessionAgent.Tests/SnapTapWorkerProgressTests.cs
---

## Decided
Driver keyboard hook setup now leaves on a stop between hooks. RazerSnapTapHookSession checks shouldStop before each SetHook. RazerSnapTapDriverTransport passes canSetHook as the native canSubmit, so the stop is checked after per-mutation admission and immediately before DeviceIoControl. Both throw KeyboardSubmissionDeferredException, which the session reports as SubscriptionDeferred with MayHaveSubmitted=false. The worker then takes the existing clean-pause exit 8, and the guardian clears the partial set from evidence. Exit 8 after an input heartbeat timeout whose Paused status never reached disk now goes through the strict IsKilledStall gates. It is refused if the worker published any non-clean-pause failure (MatchingWorkerFailure). The clean and killed stall branches are exclusive.

For stall diagnosis, the worker records its step in a named shared-memory section, SnapTapWorkerProgress (Local\OpenBlade.SnapTap.Progress.<supervisorPid>). The supervisor logs it on a heartbeat timeout as "SnapTap worker heartbeat timeout: ... WorkerStage=...; StageElapsedMs=...; HooksSet=...; LastAdmissionMs=...; SlowestStage=...; Outcome=...". The failure record is not changed.

## Tried and ruled out
Logging from the worker: the worker registers no trace listener on purpose, because log appends take a cross-process mutex with a 2 s wait and must not run on the input thread. A killed worker could not log anyway. Putting the stall context into HeartbeatTimeoutFailure.Context: IsCleanStall and IsKilledStall match that record by equality.

## Watch out
The root cause of the 2026-11-27 stall is still unproven. The next slow boot's "SnapTap worker heartbeat timeout" line in OpenBlade.SnapTap.log names the step. OwnershipRecord with a large StageElapsedMs means the write-through evidence flush; HookMutation with a large LastAdmissionMs means admission. The stop check cannot help if the worker is blocked inside a single IOCTL or flush. A perl replacement containing \.\NUL fails with "Missing braces on \N{}"; use the Edit tool for such literals.
