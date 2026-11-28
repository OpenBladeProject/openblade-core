---
title: Keep fatal admission failures distinct from refusal
author: Jack
date: 2026-11-12
branch: fix/native-thermal-review-corrections
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_native.rs
---

## Decided
Expected fresh admission refusal is a bounded Blocked result. Poisoned lifecycle/controller state is a typed fatal admission failure, including lifecycle poison before a WindowsGuard can be captured. Check health before expected capture exits, around sampled authorization, and after awaited mutation. Propagate only the typed internal failure through the factory; normal session/controller uncertainty remains refusal. A post-submission fatal error preserves FirstIntent and stops the outer service without replay.

## Tried and ruled out
Fresh Option alone collapses controller lock poisoning into ordinary absence. A health check solely on a constructed guard misses pre-capture lifecycle poison. Do not convert all capture errors to fatal or weaken storage/origin checks.

## Watch out
Injected observation tests must perform the same metadata readiness GetStatus as installed prepare before observing or taking a publication feed. The A/B test warms up before either request, never between the two subscription events. A read checkpoint checks admission both before and after port verification, so a post-reservation failure fixture must permit both initial checks. These are test setup corrections; production safety checks remain intact. Original failed exact-head logs are retained separately.
