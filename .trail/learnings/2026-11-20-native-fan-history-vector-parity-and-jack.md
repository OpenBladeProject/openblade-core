---
title: Native fan-history vector parity and limited-power deferral
author: Jack
date: 2026-11-20
branch: feat/native-reconcile-main-ipc
touches:
  - tools/openblade-windows-host/tests/data/fan-history-v3-limited-source.json
---

Supersedes 2026-11-20-native-protocol-85-reconciliation-with-main-jack.md, which is stale on three points, and the limited-adapter watch-out in 2026-11-19-native-fan-handback-is-not-a-jack.md.

## Decided
- The native HID performance writer admits every supported AC preset on both classified external classes, full (PluggedIn) and limited USB-C, with the same commands (d6ce3339). It is no longer Balanced-only on USB-C.
- Native configuration reads committed aggregates at schemas 25 to 27 and writes 27/10. The persisted fixtures were regenerated from managed at 27/10, and frozen v8 override and v25 profile copies keep the older-schema inputs.
- The three Service.Tests that were red (ConfigurationInspection, ProfileFixture, ConfigurationAggregate) pass on this branch.
- `defer_manual_performance` defers on power 0 and 2, matching managed `PowerSourcePolicy.IsExternal`. An owned fan row on a limited adapter now gets managed's Stored deferral, not Blocked.
- The native aggregate reader applies managed `PreserveLegacyIndependentProfileIntent` to schema-26 lighting (power profiles and application overrides, case-sensitive members). It is read-only.

## Tried and ruled out
- Minting a new fan contract id for selector 2 with source 2: unnecessary. Pre-d6ce3339 managed readers reject such rows as invalid history, which fences retirement fail-closed.

## Watch out
- Managed and Rust fan-history readers must consume the same vector. Any change to FanRecord validation needs a case in `tools/openblade-windows-host/tests/data/fan-history-v3-limited-source.json` (or a sibling vector) checked by both `fan_history` tests and `NativeThermalHistoryTests`. A Rust-only relaxation previously left `WindowsNativeFanHistory` refusing valid native rows, which fences retirement, upgrade and mailbox custody for good.
- `configuration-schema26-lighting.json` in WindowsReplay/v1 is likewise shared by the managed store oracle and the native Storage test.
- `fan_rig(..)` with `actual = 10` is the limited adapter (10 < recommended 15); `actual = 0` is battery.
