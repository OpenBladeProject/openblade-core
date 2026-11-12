---
title: Read compact Wave without guessing direction or host ownership
author: Jack
date: 2026-11-12
branch: fix/blade14-lighting-readback
touches:
  - Directory.Build.props
  - docs/experimental-blade-support.md
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
  - src/OpenBlade.Protocol/FirmwareQueryDiagnostics.cs
  - src/OpenBlade.Protocol/IBladeDevice.cs
  - src/OpenBlade.Protocol/IProductionLightingPort.cs
  - src/OpenBlade.Service/ProductionLightingSession.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/WindowsVerifiedBladeDeviceProvider.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeLightingTests.cs
---

## Decided
For issue #945, recognize an exact compact Wave effect reply as a known effect with unknown direction across the shared experimental lighting backend. Do not fabricate a complete LightingSettings. Direct built-in destinations are permitted after fresh admission; host frames still need a complete restoration baseline. Apply common software fixes across affected models rather than restricting them to the reporting laptop.

Exact PI4110 MCU 1.04.00 (02C5) and PI490 MCU 1.04.00 (029D) handlers write direction at payload byte 3 while retaining request DataSize. Use four-byte GET [1,5,0,0] only for these independently reviewed model contracts; retain three-byte requests on 029F/02A0. Both Wave setters consume direction byte 3 unconditionally and hardcode speed 40. Keep the existing four-byte SET; omitting direction supplies zero, not preserve-current semantics. See docs/compact-wave-readback.md, the two immutable-image proof results and tools/firmware/verify_blade_lighting_readback.py.

The service must ask the actual admitted backend whether a selected setting needs host renderer ownership. Native effects report ownership None; successful writes must not become renderer failures or automatic replay candidates. Off must still confirm absence of an owned renderer, and host frames must retain all ownership checks.

## Tried and ruled out
Do not invent Rightward from compact readback, issue a three-byte Wave SET as an unspecified direction, borrow 0255's 03/8A color getter, request unrepresented speed, or extend the longer GET to unreviewed MCU contracts. No default-effect bridge is needed. Keep strict extended-reply validation and ambiguous-write pauses.

Trail check's lexical matches about brightness-first wake, raw-zero state and failed queries do not describe this patch: dark-wake ordering and raw-one rejection remain unchanged, compact replies are successful known-effect observations, nullable state is never used as a frame restoration baseline, and final compact Wave confirmation explicitly leaves direction unknown. The older blanket rejection of compact Wave is superseded by the pinned getter and MCU evidence.

## Watch out
Firmware source pins and simulated fixtures are separate from physical output or installed-MCU attestation. No hardware write or updater execution was performed. Keep exact-model/source and fresh per-write admission. Shared renderer classification must distinguish native Wave/random Breathing from host-rendered direction/color variants, and apply to scoped/profile/lifecycle/recovery success checks together.
