---
title: Keep hybrid lighting migration and renderer recovery consistent
author: Jack
date: 2026-11-18
branch: fix/pr-977-lighting-review
touches:
  - docs/rz09-0529-lighting-expansion.md
  - src/OpenBlade.Contracts/Lighting.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.IndependentLighting.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
  - src/OpenBlade.Protocol/ExperimentalLightingBaselineJournal.cs
  - src/OpenBlade.Protocol/Rz090528BrightnessLightingFactory.cs
  - src/OpenBlade.Service/BatteryConfigurationStore.cs
  - src/OpenBlade.Service/ControlCoordinator.cs
  - src/OpenBlade.Service/PersistentOverrideStore.cs
  - src/OpenBlade.Service/ProfileSetValidator.cs
---

## Decided
Preserve schema 26 profile and last-known preferences, schema 9 overrides, and schema 17 exports as field-only whenever IndependentControls used to suppress keyboard writes. Leave the saved effect available as context. Current schemas can combine a requested keyboard effect and independent fields. Require new field readbacks, then use completed setter evidence for unreadable host pixels and verify current renderer ownership. Confirm one physical brightness and logo target when both layers specify one.

## Tried and ruled out
A pending renderer journal needs different handling before and after possible submission. Retire a newly saved record when no write was submitted, and retain the exact baseline with faulted ownership after uncertainty. Keep protected storage and externally changed journal checks. Optional healthy accessory-read failures should not erase unrelated state.

## Watch out
Initialize per-key controls from the canonical Lighting page presentation. Compare power source and current draft when the picker closes, and carry independent zone intent alongside readable built-in keyboard state. Imports with durations shorter than the acknowledged 02C7 transport cadence can skip frames. Use the exact PR head plus a separately reviewed fix commit for verification.
