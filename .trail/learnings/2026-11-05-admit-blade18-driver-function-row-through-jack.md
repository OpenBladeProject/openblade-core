---
title: Admit Blade18 driver function row through its own Fn observation
author: Jack
date: 2026-11-05
branch: feat/blade18-source-support
touches:
  - Directory.Build.props
  - docs/blade18-source-feature-recovery.md
  - docs/keyboard-input-backends.md
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - src/OpenBlade.SessionAgent/Blade18DefaultFnInputState.cs
  - src/OpenBlade.SessionAgent/FunctionRowDriverContext.cs
  - src/OpenBlade.SessionAgent/FunctionRowDriverInputProcessor.cs
  - src/OpenBlade.SessionAgent/Rz090484FnInputState.cs
  - src/OpenBlade.SessionAgent/ServiceReactiveLightingLoop.cs
  - src/OpenBlade.SessionAgent/SnapTapDriverSupervisor.cs
---

## Decided

Expose FunctionKeyPrimary as a host-managed control only on exact RZ09-0484/02A0. The reviewed Product672 Fn grammar and shared driver API establish the source-backed path without a firmware Driver-mode or primary-swap write. Keep the 02A0 decoder separate: report4 is16bytes and either0A or0B means Fn. FunctionRowDriverContext consumes an atomic collection/state observation and rechecks exact collection, model and local session; unknown or concurrently replaced observations pass the entire physical key press through.

Use one selected F-row owner. Driver or unknown selection prevents the default Windows F-row handler from starting actions; P/R/T/navigation retain their separate existing routes. Held02A0 F-row presses participate in ownership handoff and existing owned-hook/output recovery. Persist saved primary intent with existing host-managed profile fields; effective application overrides remain authoritative at runtime. Shared draft discard must skip advertised host controls instead of invoking firmware setters.

## Tried and ruled out

A compatible02A0 keyboard interception ABI alone did not complete function-row support: the helper only admitted0581/0528 and consumed their different Fn decoder. Do not broaden that generic report grammar or inherit their firmware Driver-mode admission. A firmware UI flag is not needed for this host interception feature, but the per-model source, driver trust, topology, lifetime and stop-on-ambiguity requirements still apply.

## Watch out

For02A0, replace the generic backend description that recommends switching to Windows hook: that backend retains default shortcuts and cannot apply primary inversion. Keep this model-specific description independent of backend selection and separate from actual runtime status. Preview helper calls must stay under OPENBLADE_DEVELOPER_HARNESS so production builds remain independent of test-only methods. Source/tests and disconnected renders do not establish physical recovery for issue739 or game compatibility.
