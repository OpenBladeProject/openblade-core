---
title: Stage lighting before brightness when waking from idle
author: Jack
date: 2026-10-23
branch: fix/lighting-wake-flash
touches:
  - Directory.Build.props
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
  - src/OpenBlade.Protocol/ProductionLightingPort.cs
  - src/OpenBlade.Protocol/Rz090528BrightnessLightingFactory.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeLightingTests.cs
  - tests/OpenBlade.Protocol.Tests/ProductionLightingPortTests.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeWakeLightingTests.cs
---

## Decided
For every admitted Blade lighting backend, waking from independently confirmed zero brightness stages the requested effect before restoring nonzero brightness. Keep already-visible transitions unchanged. Validated 02E0 prepares its existing handshake and initial matrix; 02C6 prepares all rows and latch; experimental models prepare their admitted built-in effect or frame. Experimental Ambient/Audio start with an admitted black uniform frame before brightness, matching validated empty sampled effects, then accept real samples without extra brightness traffic.

## Tried and ruled out
Brightness-first wake exposed stale firmware or transition colors. Delaying sampled-effect brightness until a later Session Agent sample would make SetLighting return before its requested brightness was independently confirmed, so use the existing exact-model frame transport to stage black instead. No new command, model admission or readback claim is introduced.

## Watch out
Keep original zero brightness as restoration provenance and stop on ambiguous submission. A failed first frame must never raise brightness. Test dark experimental fixtures with a confirmed Off effect: arbitrary non-Off effects at raw zero intentionally fail typed state validation. Software ordering checks cannot prove physical color output; the wake flash still needs visual confirmation on hardware.
