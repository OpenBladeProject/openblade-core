---
title: Cooling-pad emergency limits need 2 s at the raw limit
author: Jack
date: 2026-11-22
branch: fix/cooling-pad-auto-interpolation
cost: { minutes: 4, tokens: 32675 }
touches:
  - CHANGELOG.md
  - docs/cooling-pad.md
  - src/OpenBlade.Service/CoolingPadControlRuntime.cs
  - tests/OpenBlade.Service.Tests/CoolingPadControlRuntimeTests.cs
---

## Decided
This supersedes both earlier same-day emergency notes. The user wants a fast reaction that still ignores transient spikes. The pad's 95/87 C emergency limits trip when the selected raw reading stays at or above the limit for 2 s, three consecutive one-second samples (EmergencyLimit.Confirmation in CoolingPadControlRuntime). They release when that reading drops 3 C below the limit. Each sensor keeps its own streak. A missing reading, an unselected source, a powered-down GPU and a new apply all reset it. The laptop manual curve keeps its immediate raw check.

## Tried and ruled out
Raw readings with no delay: a one-second package spike ran the pad at full speed. The 5 s smoothed reading: it approaches a steady reading gradually and rounds to whole degrees, so a steady 95 C took about 12 s from 90 C and about 20 s from 65 C to trip. A shorter time constant would still approach gradually.

## Watch out
Update both sensor limits with the non-short-circuit | so the second streak is not skipped. A test that applies at the limit must tick two more seconds before full speed is expected.
