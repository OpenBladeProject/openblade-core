---
title: Preserve cooling pad and guardian cleanup when merging main
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/ServiceControlRuntime.FanStop.cs
  - src/OpenBlade.Service/ServiceComposition.cs
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Service/ProfileStore.cs
---

## Decided
Merge current main into the Stop branch before final review: preserve CPU tuning, cooling-pad lifecycle cleanup, independent lighting, supported preset projection and all Stop contracts. Combined IPC is78; profile schema remains25 because main's independent-lighting migration is24 and the Stop rolling-policy migration is25. Advance the combined feature version frommain0.56.0 to0.57.0.

## Tried and ruled out
Choosing an entire side loses features. Union per-locale resources by key and public API changes structurally; regenerate the actual wire fixture for the combined shape. The owned-Stop shutdown early return bypassed new cooling-pad cleanup. Its separate Auto phase can outlast the earlier keyboard budget, so retain the accessory's existing independent two-second handback with fresh admission and include its result. Preserve the guardian gate lifetime and drain the pad before disposing the shared temperature source.

## Watch out
This merge is integration work, not physical validation of main's additional models or Stop lifecycle paths. Keep exact-device writes and restored settings evidence separate from software merge checks. The full branch Trail diff exceeds400KB; separately check new implementation slices. Historical manual-RPM restrictions do not describe the later independently validated direct-PWM backend, and no model scope is broadened by merging main.
