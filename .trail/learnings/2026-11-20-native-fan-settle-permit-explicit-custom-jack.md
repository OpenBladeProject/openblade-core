---
title: "Native fan settle permit: explicit Custom baseline, source fence and recheck tests"
author: Jack
date: 2026-11-20
branch: fix/native-fan-noop-owner-selector
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
---

Supersedes one claim in "native fan settle-only Stop after ...": that a power class change without a POWEREVENT is something "production never does". It can happen: a USB-C PD renegotiation with AC still online changes the 07/8C class without a power notice, and nothing is delivered after STOP_PENDING. On that path the owner's own permit still authorizes Stop, and only the recorded-source fence (`source != r.source` in performance/fan.rs) refuses before the 31 row. The test `fan_manual_readback_after_a_power_change_keeps_its_existing_outcomes` now covers it (event = false), and dropping the fence is a killed mutant.

## Decided
- The explicit Automatic baseline read passes `include_custom = true`, the same Custom getter admission as every other native observation. A Custom reply publishes the levels it read. High/High Custom with Automatic fans has no baseline: the request is Blocked, saves no intent, keeps the owner, and Stop (which reads no levels) still settles it with 51. Managed rejects that read too (`MapThermalState` throws) and keeps ownership, so Stop is the one place native is more permissive.
- The settle permit no longer compares the logon. `capture_settle` takes the owner's SID, session and LUID and checks them against the live token on every `authorize_owned_stop`, so the permit-side comparison of the copied LUID could never fail.
- `observed_words` was dropped: its HyperBoost arm was unreachable because explicit requests refuse HyperBoost before any reply.
- The per-read recheck is pinned by a held-read test (`fan_settle_stop_rechecks_admission_during_the_read`) and result admission by `fan_settle_stop_result_needs_fresh_admission`. Mutants that drop `authorize_owned_stop`, `verify_fan_configuration` or make `result_admitted` true are killed.

## Tried and ruled out
- Counting the lane clippy drop as code becoming used. It comes from `#[cfg_attr(test, allow(dead_code))]` on `SettleGuard` and its impl in thermal_native.rs, which masks test-target diagnostics.

## Watch out
- The Windows admission half (`WindowsGuard::capture_settle`, `authorize_for`) is source-reviewed only; tests use the `SettleFresh` model.
- Fixture `custom_levels` defaults to High/High ([2, 2]). Any explicit-request test under selector 4 must set admitted levels, or it gets no baseline and saves nothing.
- Known gaps, documented in docs/windows-rust-native-fans.md: HyperBoost-end restore of manual fans after a 51 close (managed shares it); a competing controller at Stop leaves 30 where managed's Automatic branch would not check it; suspend detection depends on a delivered PBT_APMSUSPEND.
