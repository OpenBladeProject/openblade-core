---
title: Keep fan power admission independent when merging external modes
author: Jack
date: 2026-11-14
branch: feat/all-external-power-modes
touches:
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Fans.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.ExternalPower.cs
---

Allowing every supported performance mode on confirmed external power does not broaden fan authority. Resolve raw Windows AC presence with the fresh adapter query, bind raw source through native submission, and retain the exact source full-adapter prerequisite only for admitted host-curve SETs through requireFullPowerForFanCurve. Preserve dynamic battery fixed-fan exceptions, per-preset ranges, readback, ownership and unavailable-intent projection from the separately reviewed thermal backend. Unchanged restricted thermal tuples may persist as data; new tuples still require admission. Pending-recovery tests must publish verified fake capabilities before attaching the pending journal, because initial runtime capabilities are unavailable; the next observation freshly loads the journal, so this setup preserves recovery and ABA assertions without weakening production validation.
