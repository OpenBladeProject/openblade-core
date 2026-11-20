---
title: Native owned Stop hands back on any external class
author: Jack
date: 2026-11-20
branch: feat/native-unclassified-external-thermal
touches:
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/fan.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
---

Supersedes three older learnings where they say owned Stop is fenced by the recorded power source: 2026-11-20-native-fan-settle-permit-explicit-custom-jack.md (the `source != r.source` fence in performance/fan.rs refusing before the 31 row), 2026-11-20-native-fan-owner-settles-on-any-jack.md (a power-class change stays fenced) and 2026-11-20-native-fan-owner-settles-on-any-jack-2.md (unclassified power and manual fans after a class change stay fenced). That fence is gone.

## Decided
- Owned Stop hands an OpenBlade-owned manual fan change back to Automatic on any external class, unclassified included, whatever source the owner's row recorded: the handback commands are the same there, and the rows keep the recorded source. The owner's selector must still match.
- Stop's own first reading is the operation's class. Under the Performance selector Stop carries no preset, so that class is pinned (`PowerFence::pin_class`, managed acPreset with `EnsureThermalWritePowerAsync`) and a class change at a setter read stops it. Under Balanced a change between external classes continues.
- The pin covers only plans that write the state pair; a Stop always does.
- Every Stop setter re-reads 07/8C: a battery reading, unknown Windows power, or unclassified power without Windows AC stops it. Classified power with Windows battery is admitted, as managed admits fan commands unchecked there.

## Tried and ruled out
- Keeping `source != r.source`: it failed closed when an owner recorded on unclassified power was later classified with no Windows event, where managed releases.

## Watch out
- A steady-state Stop cannot strand progress 31: the pin compares with Stop's own first reading, not the recorded source. Only a real class change after the 31 row leaves it pending for read-only reconciliation, with at most the first setter submitted. At the first setter native submits nothing, where managed (which skips the pin on its first command) would write one setter.
- The settle-only permit after a POWEREVENT is unchanged: it appends only the 51 row, so owned manual fans then stay outstanding.
