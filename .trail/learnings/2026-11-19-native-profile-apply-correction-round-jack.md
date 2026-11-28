---
title: native profile apply correction round
author: Jack
date: 2026-11-19
branch: feat/native-agent-display-profile
---

## Decided
- Native applyProfile decides each later field from the previous step's reply query, not one query; managed decides it from the setter's own result snapshot (`result.State ?? QueryAsync`). This is a declared deviation: when the two differ native follows the managed rule on the current display state. Never decide a field from an unavailable snapshot: `profile_apply::apply` repeats the availability check before every configured stage and fails like managed `Failure(state.Message ?? "Display settings are unavailable.", state, submitted)` (Failed, or Indeterminate after a submission). A no-op step's unavailable reply previously let HDR be skipped and color written blind.
- `Snapshot::observes` treats HDR as observed by any available snapshot, an unsupported panel included (an available snapshot always completed the advanced-color read). Requiring `hdr_supported` trapped a profile with an HDR setting on an SDR-only panel after an Agent restart: the durable record stays paused (managed `HasCompleteReadback` needs support) and reloads the gate, and no read could clear it. setHdr refuses before submission while unsupported and the profile skips HDR, so nothing can stack on that state. The durable `confirms` stays managed.
- Brightness keeps `HasCompleteReadback` for the gate: while it is unreadable every request writing brightness fails in managed too, so the gate holds no request that could succeed.
- Fatal-query recording is not always Indeterminate: an unconfigured profile is recorded Succeeded and a power-refused one Blocked (nothing submitted), then the error is returned. Deviations 1, 3, 5 and 6 report MORE certainty than managed (Failed/Blocked instead of Indeterminate), only when nothing was submitted.
- Managed requires `refreshRateKey`, `hdrEnabled` and `colorProfileKey` in applyProfile settings; only `brightnessPercent` and `isConfigured` may be missing (the earlier learning said "missing members"). Native requires all five.
- Nested derived request structs go through `display_query::Object<T>` (map-only visitor): serde's derive accepts a positional JSON array, System.Text.Json does not. Battery.v1's request is a hand-written map visitor and is already object-only.
- The pause gate and checkpoint (`write::begin`, returning `Refused`) and `write::lock` are shared by brightness, the CCD/WCS frame and the profile; `WindowsHost::{hdr,refresh,color}_writer` build the production hosts for both the single writes and the profile.

## Tried and ruled out
- Clearing HDR uncertainty in the durable record when the profile succeeds: the durable record must stay managed.
- Removing the redundant `|| agent_profile` admission terms: kept, they survive a reordering of the checks and cost nothing.

## Watch out
- The request fixture has 71 rows (23 valid, 48 invalid); `request_parity_tests` pins the count. The all-null settings without `isConfigured` is a managed-lenient row: a `#[serde(default)]` on `is_configured` survives every other row.
- The earlier "14688-case differential differs only in deviation 2" holds only for that static-display harness. The reviewer's environment differential (displays that change between steps, 4.75M cases) also shows deviation 7: about 19,300 cases where native submits a field managed does not, all decided from an available reply (none blind after this round).
- Mutants against the old index-based `select` (`V01`, `V35`) no longer apply; `Stage::ORDER` and the per-stage availability check have their own mutants.
