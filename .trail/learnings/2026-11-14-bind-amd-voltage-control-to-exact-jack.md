---
title: Bind AMD voltage control to exact native contracts and retain legacy recovery
author: Jack
date: 2026-11-14
branch: feat/cpu-undervolt-models
touches:
  - AGENTS.md
  - Directory.Build.props
  - docs/rz09-0528-cpu-tuning.md
  - docs/source-reviewed-blade-models.md
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - src/OpenBlade.Protocol/Rz090528CpuFreshUsbIdentity.cs
  - src/OpenBlade.Protocol/Rz090528CpuLiveCurveNativeTransport.cs
  - src/OpenBlade.Protocol/Rz090528CpuLiveCurvePlan.cs
  - src/OpenBlade.Protocol/Rz090528CpuLivePowerPlan.cs
---

## Decided
CPU Voltage Optimizer on exact RZ09-0508/8945HS and RZ09-0482/7940HS uses the independently reviewed signed AMD V22 SDK 2.12 package already running. Keep closed per-model CPU, PCI, fuse, PM layout, E1 ordinal getter and 53 setter contracts. Require fresh capability82 bit2 before every Phoenix E1 vector, actual 5000A Min/Max/Stepping admission and complete unchanged Fast/Slow/STAPM peers. The source review is evidence/source-reviewed-blades/2026-11-14-cpu-undervolting.md.

## Tried and ruled out
Do not load the Razer scalar wrapper: initialization can remove/install drivers, its getter collapses mixed core values and the JS getter callback can write. No better replacement for the independently measured 0528 V31 native route was established. Shared planning and recovery use semantic per-core vectors; schema3 remains the exact legacy V31 contract and schema4 explicitly resolves a closed contract. 0530 remains observed-only pending its ten-core contract.

## Watch out
Never cache a guessed legacy contract after an initial unavailable host read, or let that normal pre-native refusal poison the shared native worker. Retain one positively bound session/latch and test unknown-to-known retry on the actual provider/coordinator. Source review and offline fixtures do not establish on-device stability or restoration. Unknown PM layouts and changed raw driver recipes remain unavailable; do not create or start a driver while discovering support.
