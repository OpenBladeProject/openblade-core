---
title: Native applies stored performance on fan handback
author: Jack
date: 2026-11-20
branch: fix/native-performance-apply-on-fan-handback
touches:
  - docs/windows-native-thermal-mutation.md
  - docs/windows-rust-native-fans.md
  - AGENTS.md
---

## Decided
- Native now applies the stored performance intent on fan handback, closing the cutover gate in docs/windows-native-thermal-mutation.md. Supersedes the "deferral parity is reply-only" decision in 2026-11-19-native-fan-handback-is-not-a-jack.md.
- Managed's trigger is the fan request itself: on exact RZ09-0581 (`SupportsDirectManualFanTransitions`, no `ReportsPerformanceWithManualFans`) `ApplyDirectAutomaticFanTransitionOutcomeAsync` returns manual fans to Automatic by writing the resolved preset's own 0D/02 pair, when the saved fan intent before the request was manual (`previousManual`). Native mirrors that: `Plan::select_toward` takes the saved preset's selector for a non-manual intent, so it is one direct pair with the same bytes as `PresetPair`, not a fan pair then a preset pair.
- `FanPermit::resolve_fan` reads `desired_manual_fans` before `save_fan`, resolves the destination with `handback_preset` (gpu_incomplete, then `Storage::desired` for the freshly read class) and `authorize` rechecks that preset before every setter, like the saved fan settings.
- Fan journal grammar is unchanged (selectors 0|2, sources 0|2 were already valid), so the managed fan-history readers and the shared vectors need no change. The row records the destination selector.
- Stored deferral text is now managed's exact `DeferredPerformanceIntentMessage` in builds with service-fan-mutation-v2; the thermal-only build keeps the resubmission instruction, since it has no native fan request.

## Tried and ruled out
- A separate performance pair after a confirmed fan Automatic readback: managed never does it, and AGENTS.md Direct transitions forbids the extra write. A "power change between handback and apply" therefore means between resolution and a setter of the one pair.
- Applying at Stop cleanup: shutdown keeps the owner's selector (AGENTS.md handoff rule). Owned cleanup still uses `Plan::select`.

## Watch out
- `fan_rig(selector, ..)` now saves a profile preset matching the observed selector on both external classes; otherwise every explicit Automatic test retargets to Balanced. Use `fan_rig_with`/`handback_rig` for divergent presets.
- Silent, Custom and other presets managed would write on handback stay unapplied natively (the writer admits Balanced/Performance only); a saved GPU clock effect keeps the observed selector and the reply says so.
- A native-to-managed handback of a pending handback row names family fan, fanMode Automatic only; managed confirms fans and reconciles performance from its own desired state.
- Feature-lane clippy (`--bin openblade-windows-service-candidate --features ... --all-targets -D warnings`) is red at base too; compare diagnostics against base, not zero.
