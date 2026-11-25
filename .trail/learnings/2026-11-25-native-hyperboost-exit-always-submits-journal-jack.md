---
title: Native HyperBoost exit always submits; journal reader fails closed
author: Jack
date: 2026-11-25
branch: feat/native-owned-hyperboost
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/hyperboost_mutation.rs
  - tools/openblade-windows-host/tests/data/hyperboost-journal-vectors.json
---

## Decided
- A native owned HyperBoost exit always submits where managed does (restoreIntentSkipped, ServiceControlRuntime 7118-7127): when the saved intent has GPU clock tuning or no destination at the class, it writes managed's Balanced restore pair (ownedHyperBoostBaseMode ?? Balanced, 7066), stores nothing, keeps the saved intent, and names the skipped part in the reply (hyperboost_mutation Skipped).
- Each restore-only setter is admitted by thermal_mutation::admits_restore_class (classified class agreeing with Windows, battery included); destination_holds does not re-resolve the saved intent for it.
- journal_safe fails closed: exact camelCase members once, JSON numbers, every member typed in every schema, full timestamp. Managed's verdicts are recorded in tools/openblade-windows-host/tests/data/hyperboost-journal-vectors.json by NativeHyperBoostJournalVectorsTests; regenerate with OPENBLADE_REGENERATE_NATIVE_HYPERBOOST_JOURNAL_VECTORS.
## Tried and ruled out
- Letting the restore-only exit run while an owned fan change is outstanding: thermal_history admits no performance row while a fan owner is outstanding, so Storage::open fails afterwards and owned Stop cannot hand the fans back. FanHandbackPending still refuses the exit before any row.
## Watch out
- Old fixtures like restoreFanControl {"mode":0} or a journal without required members are refused by managed itself; build fixtures from the recorded written/balanced vector.
- WriteConfirmationPolicy.MatchNativeHandback is reached only from native handback entries (PendingWriteConfirmationTracker), so a HyperBoost reading there is a Mismatch, not Unavailable.
