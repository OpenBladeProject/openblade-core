---
title: Preserve Phoenix post-write drift and signed restoration admission
author: Jack
date: 2026-11-15
branch: feat/cpu-undervolt-models
touches:
  - evidence/source-reviewed-blades/2026-11-14-cpu-undervolting.md
  - src/OpenBlade.Protocol/CpuPhoenixCurveNativeWire.cs
  - src/OpenBlade.Protocol/Rz090528InstalledCpuBackendInspector.cs
  - src/OpenBlade.Protocol/WindowsRz090528CpuLiveCurveAdapter.cs
  - tests/OpenBlade.Protocol.Tests/CpuPhoenixCurveNativeWireTests.cs
---

## Decided
Retain a valid Phoenix post-setter PM snapshot even when its exact raw Fast/Slow/STAPM peers changed. Complete its independent version bracket and report ReadbackDrift with the acknowledged disposition and full curve readback. Before-setter peer drift still prevents submission, and any post-setter ambiguity or drift stops further writes.

## Tried and ruled out
The exact V22 per-core setter sign-extends the offset at Device.dll RVA53668, negates it at 5367C, then passes it through C6A0 to the unsigned C620 range checks. Do not replace negation with Math.Abs: a positive original +3 is not restoration-admitted, so the entire forward operation must remain denied before the first setter. CPUID EBX[31:28] is package type, not package count; source family/model evidence does not establish an exact stepping restriction. Fresh identity is already checked at acquisition and immediately before the setter.

## Watch out
Test both Blade 14 profiles with post-write drift: one acknowledged setter, two PM copies, retained complete curve readback and ReadbackDrift rather than Transport. Test positive-original refusal without a setter. The separate legacy no-peer wire is RZ09-0528/V31 only, with its original fixed command indices. A review's code duplication or hypothetical future layout concerns do not justify widening a closed native contract or a broad executor rewrite.
