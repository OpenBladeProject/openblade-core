---
title: Publish the classified source when releasing a power-only block
author: Jack
date: 2026-11-21
branch: chain/997
touches:
  - src/OpenBlade.Service/ServiceControlRuntime.UnclassifiedExternalPower.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
---

## Decided
Releasing a stale power-only write block at a classified power change must publish the classified ActivePowerSource in the same snapshot. Stacking PR 997 on PR 991 exposed one revision with ExternalUnknown and WritesBlocked=false, which PowerChange_WithoutPending_KeepsAPowerOnlyBlock rejects.

## Tried and ruled out
Publishing current with WritesBlocked=false alone, as ReleaseWriteBlockHeldByPendingConfirmations does. That path only runs under an already classified source; this one runs while the published source is still the unresolved presentation.

## Watch out
Textual merge-tree checks between PRs miss this. Run the full Service suite on the stacked tip.
