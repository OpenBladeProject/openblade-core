---
title: Native fan owner settles on any power source
author: Jack
date: 2026-11-20
branch: fix/native-fan-noop-owner-selector
touches:
  - rust/crates/openblade-domain/src/fan_recipe.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/fan.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
  - docs/windows-rust-native-fans.md
---

## Decided
- Supersedes the source and plan conditions in 2026-11-20-native-fan-owner-settles-on-any-jack.md. Fan ownership settles at 51 with no setter on `fan_recipe::automatic_handback`: a fresh, agreeing 0D/82 pair with Automatic fans under a stock selector (Balanced, Performance, Silent, Battery Saver, Battery Balanced), on any classified power source. Neither the recorded source nor a native `Plan` is required. Managed basis: `ExactFanControlOwnership.ObserveTopology` ignores power.
- Stop checks the predicate before any plan, so battery never reaches a setter path. The explicit request path treats an Automatic intent as a no-op when the plan is unchanged or absent; a selector-changing plan still writes the saved preset.
- The 51 row repeats the owner's exact record, including its recorded source, so the grammar and both readers are unchanged. Battery source 1 never enters a fan record.

## Tried and ruled out
- Recording the current source in the 51 row: the grammar requires the exact owner row, so it would widen both readers for no benefit.

## Watch out
- Still fenced: Custom and HyperBoost Automatic, unclassified 07/8C power (recommended 0), disagreeing channels, manual fans after a class change.
- `resolve_fan` no longer filters by plan; `Reader::apply_fan` owns plan selection. A no-plan manual intent still fails at `plan?`.
- The reply names a carried preset as matched only when firmware confirms it.
