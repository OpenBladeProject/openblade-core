---
title: Native thermal control on unclassified external power
author: Jack
date: 2026-11-20
branch: feat/native-unclassified-external-thermal
touches:
  - docs/windows-native-thermal-mutation.md
  - rust/crates/openblade-domain/src/automation.rs
  - rust/crates/openblade-domain/src/fan_history.rs
  - rust/crates/openblade-domain/src/fan_recipe.rs
  - rust/crates/openblade-domain/src/thermal_mutation.rs
  - rust/crates/openblade-domain/tests/automation_policy.rs
  - rust/crates/openblade-protocol/src/lib.rs
  - src/OpenBlade.Deployment/WindowsNativeFanHistory.cs
  - tests/OpenBlade.Deployment.Tests/NativeThermalHistoryTests.cs
  - tests/OpenBlade.Deployment.Tests/OpenBlade.Deployment.Tests.csproj
---

## Decided
- Native ports managed f6502f85 (ordinary thermal control on unclassified external power) for exact RZ09-0581: Balanced/Performance and Fixed/Maximum/Automatic fans on source 3 (`automation::UNCLASSIFIED_EXTERNAL`). Unclassified = fresh Windows AC presence plus a completed 07/8C query without a class: a correlated FAILED/NOT_SUPPORTED rejection with single-report geometry (`RazerReport::is_completed_rejection`) or a success reply with an unmapped actual level or an unmapped/zero recommended level.
- `PowerFence` (performance/mutation.rs) carries the first reading's class through the operation: every setter's fresh 07/8C read must give the same source (no refinement either way), and once unclassified every checkpoint also needs AC presence. Any change stops without replay.
- No power or application profile applies on source 3 (managed `CreateObservedFallbackProfile`); explicit overrides only (`override_source_eligible`), GPU tuning rejected before storage, clears reply with managed's "waits for adapter classification" text. A fan clear first hands an owned manual change back to Automatic under the observed selector.
- Fan journal source 3 is admitted by both Rust `FanRecord` and managed `WindowsNativeFanHistory`; shared vector `tools/openblade-windows-host/tests/data/fan-history-v3-unclassified-source.json`. Older readers reject the row (fail-closed custody).
- Temporary expiry is `automation::temporary_expired`: classified sources expire on a different class; source 3 expires only battery-saved intent.

## Tried and ruled out
- Promoting source 3 to full or limited power, or changing `performance_source_eligible` (frozen C# predicate behind the FFI): managed keeps ExternalUnknown ineligible for profiles.
- Admitting a mid-pair class change that agrees with Windows (managed's performance path does): native stops on any change; listed as a difference in docs/windows-native-thermal-mutation.md.

## Watch out
- The native status base reports activePowerSource 3 on any AC, so status-time storage reads see power 3 even on classified power. With `temporary_expired`, a fan temporary saved at 0 is no longer expired by such a read (it was before).
- The mutation fixture cannot read back Custom with Automatic fans; profile tests must reset fans to [1;2] before expecting a Custom profile apply.
- Clippy `await_holding_lock` ignores `drop(guard)`; read fixture state through a scoped helper that returns owned values.
- Explicit Automatic is now admitted wherever presence holds, so a test that wants a settle-only Stop path must remove AC presence (`windows_on_battery = Some(Some(true))`), not just the class.
