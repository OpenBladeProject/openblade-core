---
title: Pending-only deferred choice message and blocked tray performance clear
author: Jack
date: 2026-11-20
branch: fix/manual-to-automatic-fan-restore
touches:
  - src/OpenBlade.Service/ConfirmedStateDriftTracker.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Tray.Core/MainViewModel.cs
---

## Decided

- A message that says a stored performance choice waits must test `HasPendingDeferredPerformanceChoice` (Pending only). An Invalidated marker still exists but the next fan request drops it with the kept message, so promising it is false.
- The tray Clear manual overrides result treats a performance clear that returned `Stored` or `Blocked` as restored when the fan clear that follows is `Applied` and the post-clear status confirms the desired performance. A shared selector without direct transitions on battery (RZ09-0528) returns `Blocked` ("Performance changes require Automatic fan mode") for the performance clear under manual fans, and the fan clear then restores the profile preset.

## Watch out

- The status check is the gate, not the operation status: `Blocked` alone also covers real refusals that leave performance unrestored.
