---
title: Recover dark experimental lighting without replay or guessed confirmation
author: Jack
date: 2026-10-23
branch: fix/lighting-wake-flash
cost: { minutes: 1, tokens: 0 }
touches:
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeFactoryTests.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeDarkWakeRecoveryTests.cs
---

## Decided
Keep confirmed raw-zero built-in lighting recoverable as unknown on the non-frame experimental backends. Recovery reads a fresh drained session without replaying writes; the service retires only the unknown lighting confirmation and holds automatic lighting until a new accepted explicit request. The next setter reasserts the complete destination before raising brightness. PID0255 confirms static and all breathing parameters while dark.

## Tried and ruled out
Do not invent Off from a selected effect at raw zero, weaken strict public/final readback, or extend nullable baselines to host-frame models that require representable restoration state. Do not reconcile an abandoned unknown lighting confirmation: stale power-source ownership can otherwise fabricate match/mismatch attribution.

## Watch out
Raw one rounded to zero and unknown effect selectors remain rejected. Read-only recovery must not clear a write pause before fresh identity, drain and service acceptance. Preserve existing drift when retiring unknown lighting, including across power-source changes. Trail keyword warnings against historical brightness-first comments and version pins are lexical; this change retains command and exact-device admission.
