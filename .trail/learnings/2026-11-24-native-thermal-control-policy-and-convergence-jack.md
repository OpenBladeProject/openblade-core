---
title: native thermal control policy and convergence status (rule 5 S3/S4)
author: Jack
date: 2026-11-24
branch: feat/native-thermal-control-policy
---

## Decided

- Native stores `thermalControlPolicy` and `thermalPolicyUpgradeDisclosure` in the per-user aggregate (`Storage::thermal_policy`, `save_thermal_policy`) and serves `setThermalControlPolicy` and `acknowledgeThermalPolicyUpgradeDisclosure` under `service-profile-triggers-v1`. No IPC protocol change.
- `thermalConvergence` is derived at each publication (`thermal_mutation::convergence`), in managed's closure order and then its classifier. A contradiction that managed would converge is published as Paused with `InternalStateUnavailable`, because native has no loop. The S5 loop replaces that row.
- The publication reads low power from the lifecycle's write epoch (`Runtime::admission_epoch`, `Lifecycle::active`), never from the gate probe. Both are absent exactly when the gate is set or the lifecycle is unreadable.
- Managed vectors: `tests/data/thermal-convergence-vectors.json`, recorded by `ServiceControlRuntimeTests.NativeThermalConvergenceVectors.cs`. Statuses replay through the strict codec in `thermal_convergence_tests.rs`; replies replay through the dispatcher in `readiness_tests/thermal_policy.rs`.

## Tried and ruled out

- Reading `Runtime::in_low_power` in the convergence projection. The trigger tests count gate reads (`suspend_after_next_check`, `calls == 2`), so a publication's extra read moved the suspend before the step's own admission and broke five `request_triggers::low_power` tests.
- Status revision as proof that a stored policy was republished. Other requests advance it too; assert the member's content instead.

## Watch out

- On AC, native status reports the coarse class 3, so the member reads PowerUnsettled. Only battery is classified until status carries a settled external class.
- The member is absent, not Paused, under a competing controller (the capture is refused), for a preserved aggregate (earlier saved-intent reads fail), and before a thermal request or startup retains the user's policy.
- `manual(desired.fans)` in front of `owns_exact_fans` is an equivalent mutant: the classifier reads ownership only when both sides are manual. The guard only skips the owner lookup.
