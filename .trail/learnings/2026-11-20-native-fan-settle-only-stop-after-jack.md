---
title: Native fan settle-only Stop after lifecycle events
author: Jack
date: 2026-11-20
branch: fix/native-fan-noop-owner-selector
touches:
  - rust/crates/openblade-domain/src/fan_recipe.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/fan.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_native.rs
  - docs/windows-rust-native-fans.md
---

## Decided
- Supersedes the Watch out list in 2026-11-20-native-fan-owner-settles-on-any-jack-2.md. `fan_recipe::automatic_handback` now also covers Custom with Automatic fans and HyperBoost (mode 7, Automatic fans). Managed maps both to `FanMode.Automatic` (ProductionBatteryPort `MapThermalState`) and `ObserveTopology` releases on that. Mode 7 with manual fans is still undecodable.
- Owned Stop tests the 0D/82 pair before reading 07/8C, so an unclassified power source no longer blocks the 51 close. The explicit request path still needs the source.
- Every SERVICE_CONTROL_POWEREVENT and SESSIONCHANGE advances the lifecycle epoch that the owner's proof pins, so the owner's own permit cannot authorize Stop after an unplug, an adapter change, a lock or a resume. Stop then uses `FanSettlePermit`, a separate type. It holds a fresh `Fresh::capture_settle` (`WindowsGuard::capture_settle` returns `SettleGuard`, whose request `authorize` is always None). That capture keeps the owner's SID, session and logon, the custody generation, origin trust, the unlocked console, the AC line status and controller absence. Its `record_fan` admits only 51, so it can never journal 31 or 40-43, and the Reader cannot send a setter without those.
- HyperBoost stays refused on the explicit path (`confirm_fan_noop`), because the reply would publish an observation outside managed read parity. Stop publishes nothing and settles it.
- The unapplied-preset hint always says "submit a new performance request to apply it". The performance writer has no fan-baseline gate.

## Tried and ruled out
- Relaxing the epoch equality in `WindowsGuard::authorize_for`: it would widen every setter path, not just the no-write close.
- A flag on `FanPermit` for settle mode: the review asked for a type that cannot reach a setter.

## Watch out
- An epoch-stale owner whose fans are still manual stays at 30 after Stop. Settling it needs a write, and the owner's proof no longer admits one; the managed handback resolves it later.
- Test rigs must deliver `lifecycle.ingress(SERVICE_CONTROL_POWEREVENT, 10)` whenever they change `actual`. Without it, power-change tests take the owner path, which production never does.
- The test Custody models lock as `current_console=false` only. Lock and unlock events alone just advance the epoch.
