---
title: Verify Fan Stop host construction and read-only availability
author: Jack
date: 2026-11-09
branch: feat/fan-stop-model-expansion
touches:
  - src/OpenBlade.Protocol/FanStopModelContract.cs
  - src/OpenBlade.Service/GuardianFanStopBackend.cs
---

## Decided
Use the model number as the diagnostic expected SKU in the six experimental Fan Stop families, matching ExperimentalBladeModels. WindowsHostIdentitySource requires a nonblank expected SKU even under ModelScoped and does not compare the machine SKU in that scope. Exercise the real identity reader with both registry sources and no machine SKU for every Fan Stop contract.

## Tried and ruled out
The two regressions reproduced independently on c1878b26 with tests added. Exact host construction threw for the first blank diagnostic SKU. Availability projection accepted an owner release and disposed its lease before controller observation. Remove that side effect instead of relying on ordering between concurrent status and controller reads.

## Watch out
Availability preserves the owner until the serialized controller accepts terminal release. A newly published client snapshot is not yet accepted evidence. Keep firmware preflight for new owners and retain the exact manufacturer, product and hardware checks. No physical-validation status was promoted.
