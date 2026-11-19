---
title: Drive the managed Fan Stop cadence test with controlled time
author: Jack
date: 2026-11-10
branch: fix/fan-stop-cadence-test
touches:
  - src/OpenBlade.FanStop.Guardian/So6120ServiceSafetySampler.cs
  - tests/OpenBlade.Service.Tests/FanStopServiceSafetySamplerTests.cs
---

## Decided
Drive the managed SO6120 cadence test from its injected monotonic clock. Have the existing test wait hook receive the same remaining interval as the production semaphore wait. Check the previous proof just before releasing each controlled 700 ms acquisition, and check that all three replacement proofs publish.

## Tried and ruled out
The sampler and test in the failed Windows Rust PR #930 job are byte-identical to its merge base and to main at 1a42133d89be. The wall-clock harness had only 300 ms of slack: a one-second start interval plus 700 ms acquisition ages the previous proof to 1.7 seconds before replacement. Runner delays can consume that slack and produce a correct fail-closed result. The log shows the expired-proof assertion but has no timestamps to identify which continuation ran late.

## Watch out
Keep the one-second start cadence, two-second freshness, production wake and cancellation semantics, and sequential source lifetime. Use wall time only as a hang bound for this regression. A full one-second wait after acquisition creates a 2.4-second proof age at the controlled assertion and must fail.
