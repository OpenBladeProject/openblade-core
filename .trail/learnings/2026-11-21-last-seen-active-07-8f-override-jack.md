---
title: Last-seen-active 07/8F override and protection-off conflicts
author: Jack
date: 2026-11-21
touches:
  - CHANGELOG.md
  - docs/blade18-observer-evidence.md
  - docs/experimental-blade-reference.md
  - docs/experimental-blade-support.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/zh-CN/experimental-blade-support.md
  - evidence/source-reviewed-blades/2026-11-19-temporary-full-charge-rz09-0529-0530.json
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Battery.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.TemporaryFullCharge.cs
---

## Decided
- Supersedes part of the 2026-11-21 note: on 0x02A0, 0x02C5 and 0x02C7 an unreadable 07/8F still blocks the limiter (07/12) when the policy last decoded bit 04 or last confirmed a 07/0F enable (ExperimentalFeaturesPolicy.TemporaryFullChargeLastKnownActive; KnownNotApplied, TemporaryFullChargeLastSeenActiveMessage). Firmware that never answered 07/8F keeps the limiter-only write.
- Protection off with bit 04 set has no confirmed representation (Synapse clears bit 04 when it disables the limiter). ConfirmedBatteryState and DesiredBatteryState stay strict; the readers publish TemporaryFullCharge = null instead: the provider battery read (isolated TemporaryFullCharge failure only on dependent backends) and both port recovery readers (ExperimentalBladePort.WithTemporaryFullCharge, InconsistentState diagnostic).
- ResolveExplicitBatteryBaseline uses the read override, falling back to OpenBlade's own override intent when it is unreadable, so an override OpenBlade enabled still blocks staging a limiter change under unclassified external power.
- The tray discharge warning keeps charge-limit suppression unless the override is confirmed active.
## Tried and ruled out
- Relaxing the ConfirmedBatteryState guard to accept protection off + override as an observed state: every consumer would have to handle a combination the planners never produce, and the cancel plan still needs protection on.
## Watch out
- OpenBlade cannot cancel that anomalous state itself (the cancel plan needs ProtectionEnabled and enabling protection is refused by the positive override read); the user needs Synapse.
- The independent-backend provider path publishes the unknown override without a separate diagnostic; only the port recovery readers record InconsistentState.
