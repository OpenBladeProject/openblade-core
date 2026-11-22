---
title: Cooling-pad emergency limits use smoothed readings
author: Jack
date: 2026-11-22
branch: fix/cooling-pad-auto-interpolation
cost: { minutes: 12, tokens: 27606 }
touches:
  - docs/cooling-pad.md
  - src/OpenBlade.Service/CoolingPadControlRuntime.cs
  - tests/OpenBlade.Service.Tests/CoolingPadControlRuntimeTests.cs
---

## Decided
The user chose to have the pad's 95/87 C emergency limits check the 5 s smoothed readings from FanCurveTemperatureFilter, not raw readings. This supersedes the same-day learning that said raw. A one-second spike no longer sends the pad to 3200 RPM. A steady 95 C reaches the limit within about 12 s from 90 C, or about 20 s from 65 C. The filter's 3 C falling deadband keeps full speed until the smoothed input drops 3 C. The laptop manual curve still checks raw readings, and the pad's missing-reading pause stays on raw.

## Tried and ruled out
Raw readings for the pad override, which made a brief package spike run the pad at full speed.

## Watch out
The filter rounds to whole degrees and approaches a steady reading asymptotically, so a test that holds a reading exactly at the limit may take over 20 ticks to cross it. Drive tests above the limit (100 C) instead.
