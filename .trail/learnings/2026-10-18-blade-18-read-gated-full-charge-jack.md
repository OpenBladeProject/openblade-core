---
title: Blade 18 read-gated full charge and evidence boundaries
author: Jack
date: 2026-10-18
branch: feat/blade18-observer-support
touches:
  - CHANGELOG.md
  - docs/blade18-observer-evidence.md
  - docs/blade18-source-feature-recovery.md
  - docs/experimental-blade-reference.md
  - docs/experimental-blade-support.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/zh-CN/experimental-blade-support.md
  - src/OpenBlade.Protocol/ExperimentalBladeFactory.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Battery.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Fans.cs
---

## Decided
Exact Product 672 inherits a complete rzDevice25 command contract for temporary full charge: GET 07/8F, SET 07/0F, oneTimeOverride bit 04. The pinned shared middleware maps Boolean to 4/0 and updates that field. This is sufficient for separately scoped experimental implementation despite the missing Synapse UI activation flag. Admit capability after valid exact-device readback; require readable policy, enabled protection and connected power for enablement, and all other mask bits clear. Confirm both the requested flag and identical raw charge-policy byte independently; pause on ambiguity without replay. Startup animation similarly uses exact inherited 0F/98 and 0F/18, with fresh readback and recovery.

## Tried and ruled out
The existing Observer recording did not enable full charge; its three zero-mask writes must not be presented as bit-04 validation. The former missing-UI-flag rule was too strict once the inherited class and middleware semantics were pinned. This does not authorize arbitrary shared SDK methods, other models, combined power masks, or physical-validation claims. Trail check reports broad keyword collisions on this large branch: default Fn shortcuts do not admit primary-mode inversion, installer manifests do not enable features, and no Synapse telemetry DLL, THX ABI or SnapTap ABI is newly admitted.

## Watch out
Keep exact-device and per-submission controller/session/ownership checks. A readable flag is source-backed experimental admission, not proof of battery charging behavior. The shared ConfirmedBatteryState contract does not represent protection-off plus active override; do not widen that contract casually for a hypothetical state. Direct protocol cancellation can preserve a readable raw disabled policy, but aggregate recovery must not fabricate a representable state. Separate fan target registers are available experimentally; stored targets are not measured RPM. Observer PR21 fixes read-only PDH inventory separately; the old ZIP still does not establish Blade18 CPU-counter availability.
