---
title: Share fan duty planning and trace Blade EC-native USB
author: Codex
date: 2026-11-09
branch: feat/fan-stop-model-expansion
touches:
  - src/OpenBlade.FanStop.Core/So6120DirectDutyCoordinator.cs
  - src/OpenBlade.FanStop.Core/So6120DirectDutyJournal.cs
  - docs/fan-stop-reader-and-ec-followup.md
---

## Decided

The duty executor and journal replay share the same entry target, field ownership and Auto restore order. Entry stages before FE; restoration checks the normal selector before touching saved staging. The two-channel journal format and native transaction policy are unchanged.

## Tried and ruled out

The separate 1532:F003 EC USB code is present on all four images. The KI890 selector report reads real selector state, but the duty-looking report reads speed or configuration RAM. Partial KI890 BIOS extraction did not establish physical USB exposure.

## Watch out

The known PNPCFG writers can close host access during power sequences, even if FE was previously observed. Their tables do not write the depth-two selector. Live admission still needs exact I2EC address binding, steady lifecycle and host ownership, or independent EC-native endpoint evidence. Selector handback alone does not restore all saved fields.
