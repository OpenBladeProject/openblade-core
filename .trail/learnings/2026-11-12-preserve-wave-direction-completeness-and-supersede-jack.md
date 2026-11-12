---
title: Preserve Wave direction completeness and supersede stale renderer recovery
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
Complete issue #945 by treating Wave wire direction 2 as a valid read-only alternate-direction observation, not a restorable built-in baseline or confirmation of requested wire direction 1. Return unavailable complete settings for compact or alternate Wave, permit an explicit built-in destination, and keep host frames blocked until their full baseline can be restored. Final partial confirmation must match exactly [1,5,4]; an extended [1,5,4,2] must fail after a requested Rightward write. Unknown direction0/255 and nonzero unsupported tails remain rejected. No direction2 SET is newly admitted.

An accepted native lighting transition must clear stale host recovery and lifecycle restoration without fabricating a host renderer snapshot. Keep Off's None check and host ownership checks. Brightness-only completion must not clear recovery merely because it supplies a non-null partial setting; preserve its previous ownership match requirement.

## Tried and ruled out
The longer source-backed GET can reveal a real opposite-direction Wave, so retaining the old blanket direction2 rejection can leave the original issue blocked. Conversely, accepting any null Wave as a successful final read would hide an ignored Rightward setter. Preserve explicit payload completeness.

## Watch out
Service scoped/profile/restore/recovery acceptance and snapshot cleanup are separate parts of the same fix. Regression tests must cover failed host effect followed by successful native effect and later observations, plus a brightness step with stale or absent host ownership. Source and fixtures do not prove physical recovery on the reporting laptop.
