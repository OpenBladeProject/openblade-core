---
title: "Native thermal presets: Custom, every fan-return destination, and managed rejections"
author: Jack
date: 2026-11-21
branch: feat/native-thermal-presets
touches:
  - docs/windows-native-thermal-mutation.md
  - docs/windows-rust-native-fans.md
  - rust/crates/openblade-domain/src/fan_recipe.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
---

## Decided
- This supersedes "Native Silent and battery thermal presets on RZ09-0581" where they disagree. Custom (5) is no longer refused at decode: it runs its selector-4 pair, the 0D/07 CPU and GPU setters and the 0D/87 final read (progress 0, 1-4, 7-10, 5). The fan return to Automatic no longer writes only Balanced or Performance: it writes every saved native destination through the performance writer's `Setters`, and the resubmit deferral text is gone.
- The fan return also plans manual fans under Silent (5), Battery Saver (3), Battery Balanced (6) and Custom (4), but only with a carried destination (`Plan::select_toward`). Managed reads them as `FanMode.Maximum` and `CanApplyAutomaticPerformanceFrom` admits every destination from it; `FanPair::state` admits selectors 0 and 2 only, so without a destination those baselines have no plan.
- An unsupported saved Custom on the fan return keeps fans manual and writes nothing, with managed's text: "Fans could not return to Automatic with the saved Custom performance intent. Performance value is not supported by the verified capability table." (`FanPermit::custom_unsupported`). Before this the plan kept the observed selector and handed the fans back silently.
- HyperBoost to a battery preset stays `Plan::NoDirectPath`. Managed's path is the restore pair (same bytes as the Balanced pair), then a full read that must confirm Balanced with Automatic fans, then the battery pair without per-command ordinary source admission. It is not "restore pair then battery pair", so it was not ported.
- Performance overrides go through the ported `ValidatePerformanceFanCombination` before storing on classified power that agrees with Windows: battery rejects Silent, Balanced, Performance and Custom (status 3), a saved ManualCurve rejects the battery presets.
- Performance and fan requests run under a 6 s recipe budget, and each operation refuses before its first row when its measured pace cannot finish (`Pace`, `Refusal::Deadline`).

## Tried and ruled out
- Killing the mutant that drops ThirdIntent and FourthIntent from the intent-append failure flag. It is equivalent: the flag is read only by `pending_reason_locked`, and after those appends fail the cached last row (4 or 8) is unsettled, so the "may have been submitted" reason wins.

## Watch out
- `fan_v2_ac_manual_deferral_is_stored_but_cleared_fan_handback_blocks` used Custom manual fans (selectors [4, 4]) to keep a handback outstanding. That baseline now returns through the profile's destination; it uses an unsupported saved Custom instead.
- The native fan path still does not run managed's ValidateThermalOverrideCombination or the FanControlPowerPolicy check of SetFanOverrideCoreAsync.
