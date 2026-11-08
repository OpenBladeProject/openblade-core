---
title: Recheck cancellation after acquiring the service request gate
author: Jack
date: 2026-11-07
touches:
  - src/OpenBlade.Service/ControlRequestHandler.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.UserActivation.cs
---

## Decided
Recheck the request cancellation token immediately after acquiring the service user-request gate, inside its try/finally and before replacing request ownership or dispatching. A canceled queued request must not activate configuration, mutate hardware or return a successful status response. Keep the ingress admission lease in its existing using scope, so cancellation releases the pending count without undoing the advanced authority epoch.

## Tried and ruled out
The existing canceled A-to-B-to-A HyperBoost regression exposed a permit-handoff race during a full test run. Relying on WaitAsync(token) alone misses cancellation that races successful gate acquisition. An inactive queued status request then skips activation's cancellation-aware await and can return normally. Do not increase timeouts or weaken the existing cancellation assertion; retain that regression unchanged.

## Watch out
Place the second cancellation check inside the acquired-gate try block: its finally must still clear request ownership and release the permit. If WaitAsync itself throws before acquisition, that finally must not run. The fix applies to all ordinary queued dispatch through HandleCoreAsync; the command-free compatibility and completed-current-user status fast paths retain their existing behavior.
