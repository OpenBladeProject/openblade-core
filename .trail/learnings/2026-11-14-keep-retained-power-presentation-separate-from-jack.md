---
title: Keep retained power presentation separate from control authority
author: Jack
date: 2026-11-14
branch: fix/issue-967-ac-control-availability
touches:
  - src/OpenBlade.Contracts/PerformanceShortcutSelection.cs
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.HyperBoost.cs
  - src/OpenBlade.Protocol/OrdinaryThermalPowerAdmission.cs
  - src/OpenBlade.Protocol/ProductionBatteryPort.OrdinaryThermalPower.cs
  - src/OpenBlade.Service/ProfileAutomationState.cs
  - src/OpenBlade.Service/ServiceControlRuntime.UnclassifiedExternalPower.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Session/DisplayAutomationCoordinator.cs
  - src/OpenBlade.Session/ThxAudioAutomationCoordinator.cs
---

## Decided
Keep public GetStatus a view of the last published snapshot. Refresh private Windows-power evidence for admission without creating a new public snapshot during canceled or rejected work. Publish presence through actual successful updates and compare with the last published value. A retained power enum is presentation provenance; RetainedPowerSource means current classification is unavailable for advanced controls, active-profile slots and automation.

Ordinary explicit controls use effective unclassified context, the reviewed capability and fresh positive presence. Apply that distinction through UI and Session Agent consumers, including display, Windows-mode and audio automation, and profile-dependent input. Source-free shortcut selection may choose only actually admitted ordinary presets. An explicit Custom cycle may request the exact-capability-checked product default if no explicit or complete confirmed ordinary settings exist; it must not borrow a power-profile slot or native tuning.

## Tried and ruled out
Do not treat a pending adapter read, cancellation, missing device or failed Windows capture as a completed unavailable classification. Do not fix reference-identity regressions by relaxing observation/cancellation tests. Do not remove fresh per-SET power checks because they can fail after an earlier confirmed SET: stop and reconcile instead.

HyperBoost shares ordinary-looking commands. Disable unclassified admission before its Maximum Fans cleanup and mode writes, not merely at the final mode-7 setter. A valid terminal adapter-read rejection does not authorize a HyperBoost request.

## Watch out
Clearing an owned Fixed, Maximum or Curve fan override on unclassified power must use the same guarded fan-only Automatic handback. A Performance clear cannot invent Balanced or a destination profile, so it remains deferred. Refresh expired presence in the existing owned-curve path before its early availability check; avoid a new timer for this. Preserve non-power journal/recovery/controller/session blockers independently of adapter availability, and never turn another user's dormant override into fresh request authority.
