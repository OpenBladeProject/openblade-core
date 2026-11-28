---
title: Owned native HyperBoost on RZ09-0581 and its journal handback
author: Jack
date: 2026-11-25
branch: feat/native-owned-hyperboost
---

## Decided
- Owned native HyperBoost (`service-hyperboost-v1`, `setHyperBoost`) is one performance step under the thermal gate, with destination 100 on the pair chain and no requested override. It stores no intent. Exit writes the saved destination at the fresh class, directly or through H1's restore recipe.
- Journal decision (b): native keeps its own rows and hands back through the existing handback, family `hyperBoost` (pending 1-4, abandonedPrepared 0). Managed latches a pending HyperBoost confirmation matched by `MatchPendingHyperBoost`. Native reads managed's `hyperboost-ownership.json` read-only (`hyperboost_journal::journal_safe`), before the request and before each setter, and refuses unless it is absent or Relinquished. Native never writes managed's journal.
- Only the pad-less path is admitted. The pad (1532:0F43) is observed by HID hardware IDs without opening anything; absence needs a complete, fully readable scan. A pad refuses entry with a native-only reply.
- The managed gate order is kept: the port's entry-base precondition (`ProductionBatteryPort.cs` 914-922) runs last, after intent, external power and pad absence. So a battery preset with Automatic fans reports external power on battery and the base text on AC.
- Native exit text is "HyperBoost is off and the prior <PerformanceLabel> performance is restored." Managed adds "and fan intent", but native restores no fan intent.

## Tried and ruled out
- Citing ServiceControlRuntime.cs lines from the base commit: they shift with any managed edit in the same branch. Cite the branch's own lines and recheck them after managed edits.
- Treating managed's CustomAutomatic entry Failed (3) as a fixture bug. Managed's journal `IsWellFormed` requires a Custom restore intent for a Custom base, so a confirmed Custom with a non-Custom saved intent fails before any write. That is real managed behaviour; native enters, and the deviation is asserted.

## Watch out
- `accept()` must set `permit.destination`, `selected_source`, the triggers baseline, `accepted` and `power`, or `record_progress` fails with "permission denied".
- `rig_at` selector 1 does not decode. Use 0 Balanced, 2 Performance, 5 Silent, 3/6 battery presets, 4 Custom (levels set by `rig_at`), 7 HyperBoost; fans 1 is manual.
- The managed recorder's fake counts a KnownNotApplied precondition throw as a HyperBoost write; no setter was submitted.
- Moving `journal_safe` out of the convergence loop broke the convergence-lane tests' `RELINQUISHED`, which only that lane compiles. Run `service-thermal-convergence-v1` after touching shared helpers.
