---
title: Confirm Blade 15 wake color before restoring brightness
author: Jack
date: 2026-10-23
branch: fix/lighting-wake-flash
touches:
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeLightingTests.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeWakeLightingTests.cs
---

## Decided
PID 0255 single-zone Static and single-color Breathing must confirm the applied effect and standard-effect color parameters while confirmed brightness remains zero, before the dark-wake path raises brightness. Their non-echo setter acknowledgement does not independently confirm application. Generic built-ins already confirm effect before brightness; host matrices do not claim unavailable color readback.

## Tried and ruled out
Waiting for the final combined lighting read permits a stale effect to become visible if a parameterized setter is acknowledged without applying. Calling that combined getter earlier also fails deliberately because the shared LightingSettings contract cannot represent a non-Off effect at raw zero. Use the existing separate effect/standard getters and compare only their readable fields.

## Watch out
The breathing parser receives the target brightness only to construct its typed parameters container; this is not a brightness observation, and only its independently read parameters are compared. Brightness remains independently confirmed at zero until the existing guarded setter/readback runs. Cover ignored application and wrong color parameters, preserve pause/no-replay on failure, and leave already-visible transitions unchanged.
