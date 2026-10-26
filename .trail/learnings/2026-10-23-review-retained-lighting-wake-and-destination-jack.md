---
title: Review retained lighting wake and destination scoped dark recovery
author: Jack
date: 2026-10-23
branch: fix/lighting-wake-flash
cost: { minutes: 1, tokens: 0 }
touches:
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
  - src/OpenBlade.Protocol/ProductionLightingPort.cs
  - src/OpenBlade.Protocol/Rz090528BrightnessLightingFactory.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeDarkWakeRecoveryTests.cs
  - tests/OpenBlade.Protocol.Tests/ProductionLightingPortTests.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeFactoryTests.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.cs
---

## Decided
A confirmed raw-zero to visible transition must bypass renderer-retention fast paths on both validated lighting backends so the destination frame is staged while dark. PID02C6 can retain Static ownership after its brightness step reaches zero; PID02E0 can observe external zero before delayed stable-Off relinquishment. Built-in destinations on every experimental model can accept a known effect at actual raw zero as an unknown baseline, reassert the complete requested effect, then confirm before brightness. Host-frame requests still require a representable restoration baseline.

## Tried and ruled out
The earlier dark-recovery note restricted nullable state to non-frame models. That model-level restriction left029F/02A0 built-in requests stuck after deferred brightness failure; scope by destination command path instead. Idle cleanup already retires validated renderers, and PID02E0's own brightness step already clears ownership, so do not claim those paths reproduce retained-frame wake. Do not roll back an uncertain experimental write, remove necessary before-visibility parameter reads, or extract a cross-backend abstraction solely for similar code.

## Watch out
Unknown frame readback remains null. Explicit resume abandons only lighting pending state without match/mismatch attribution and holds automation until new accepted intent; it does not confirm the interrupted write. Built-in recovery does not admit host frames from an unreadable baseline, raw-one rounding, unknown selectors, or new commands. Claude Opus5.5 high reviewed2169d9ab read-only; confirmed recovery and retention findings were fixed with targeted tests. Its style suggestions and speculative rollback were not correctness requirements.
