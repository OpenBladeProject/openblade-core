---
title: Keep Phoenix CO power peers raw and legacy PPT writes strict
author: Jack
date: 2026-11-14
branch: feat/cpu-undervolt-models
touches:
  - docs/cpu-undervolting.md
  - evidence/source-reviewed-blades/2026-11-14-cpu-undervolting.md
  - src/OpenBlade.Protocol/Rz090528CpuLivePowerPlan.cs
  - src/OpenBlade.Service/CpuLiveCurveOwnershipJournalStore.cs
  - src/OpenBlade.Service/CpuLivePowerOwnershipJournalStore.cs
  - src/OpenBlade.Service/CpuTuningController.cs
  - tests/OpenBlade.Protocol.Tests/Rz090528CpuLivePowerPlanTests.cs
  - tests/OpenBlade.Service.Tests/CpuLiveCurveOwnershipJournalStoreTests.cs
  - tests/OpenBlade.Service.Tests/CpuPowerTestData.cs
---

## Decided
Phoenix/Hawk Point Curve Optimizer power peers preserve the three original IEEE754 words through independent readback and durable post-boot reconciliation. Nonnegative finite watts and zero STAPM are observations, not permission to write PPT. Milliwatts are nullable display data; exact raw words determine peer equality. Keep the legacy RZ09-0528/V31 PPT encoder, write admission and schema-3 decoder unchanged.

## Tried and ruled out
Do not reuse the V31 integer-milliwatt encoder to validate V22 CO peers. Exact firmware 45.0f is 0x42340000 while 45000 * 0.001f yields 0x42340001; 54.0f and 60.0f have the same mismatch. The pinned V22 source copies raw IEEE watts and its CO setter does not require that encoder or positive STAPM.

## Watch out
Acquisition and durable receipt decoding must use the same exact model contract. Rounding display values must not hide a one-ULP change or +0 to -0 STAPM drift. Preserve the original bits and test real schema-4 persist/reconcile/archive, not only a helper decoder. This observation grants no new power setters or development hardware authority.
