---
title: Recheck request cancellation after acquiring the dispatch gate
author: Jack
date: 2026-11-07
branch: feat/cpu-voltage-completion
touches:
  - src/OpenBlade.Service/ControlRequestHandler.cs
---

## Decided
Recheck caller cancellation immediately inside the try after acquiring ControlRequestHandler's userRequestGate. SemaphoreSlim release may win against the asynchronous cancellation continuation and return a successful wait for an already-canceled token. The acquired permit still needs finally cleanup; the ingress admission lease must also dispose normally. Keep the advanced user/thermal epoch revoked.
## Tried and ruled out
Do not weaken the canceled A-B-A regression to accept an ordinary status response, move the cancellation check between WaitAsync and try (permit leak), restore an old epoch, or add a new gate abstraction solely for this check. Hosted CI failed the different-account theory; a focused local run reproduced both account cases before the fix, then passed both with the post-acquisition check.
## Watch out
Inactive GetStatus remains allowed read-only and can return synchronously without reaching token-aware activation. That is why the missing cancellation check was observable. Same active-user cached reads and protocol51 compatibility retain their existing separate paths. The shared fix benefits every model and adds no hardware authority. The .NET10 SemaphoreSlim source at lines715-750 explicitly allows this release/cancellation race.
