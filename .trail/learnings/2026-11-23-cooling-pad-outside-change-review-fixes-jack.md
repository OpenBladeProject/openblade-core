---
title: Cooling pad outside-change review fixes
author: Jack
date: 2026-11-23
branch: fix/cooling-pad-false-rejection
cost: { minutes: 26, tokens: 184967 }
touches:
  - docs/cooling-pad.md
  - src/OpenBlade.Service/CoolingPadControlRuntime.cs
  - src/OpenBlade.Service/ServiceControlRuntime.CoolingPad.cs
  - src/OpenBlade.Tray.Core/CoolingPadViewModel.cs
  - tests/OpenBlade.Service.Tests/CoolingPadControlRuntimeTests.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntime.CoolingPadLifecycleTests.cs
  - tests/OpenBlade.Tray.Tests/CoolingPadViewModelTests.cs
  - AGENTS.md
---

## Decided
- A native local-admission deferral during the cooling-pad Apply is Failed, not Blocked: by then the request, resume record and journal are saved, so Blocked would let maintenance apply a change the tray was told was refused. Only checks that run before SaveSettingsAsync may return Blocked.
- Outside-change restore needs both MaintainDesiredState and a disclosure that is not UpgradeDisclosurePending (same rule as laptop thermal convergence). A lighting outside change is restored only with AlwaysOverrideExternalLightingChanges; without it the pad releases (AGENTS.md rule 5).
- A restore apply that comes back Blocked releases control instead of trying again at every check.
- The comparison read defers at most OutsideReadFailureLimit (3) timeouts or plain IOExceptions in a row. A CoolingPadOperationException (conflict, identity) is never deferred and reaches Fail.
- The tray treats a retry that finds the view model busy as a retry (retryPending), so it keeps its refusal count. Release cancels a waiting retry.

## Tried and ruled out
- Re-reading the snapshot after a Fixed lease write to refresh the expected fan record: SetFan already confirmed (Synapse, Medium, rpm) by readback, so `record with { ConfiguredRpm = demand }` is exact and saves a full read.

## Watch out
- Resumption after a handback still reapplies saved lighting without the lighting opt-in. That predates this branch and is not fixed here.
- The restore allowance (3 in 10 min) is in memory only and resets on a service restart.
