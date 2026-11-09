---
title: Settle blocked live CPU journals only from exact originals after boot
author: Jack
date: 2026-11-07
branch: feat/cpu-voltage-completion
touches:
  - docs/rz09-0528-cpu-tuning.md
  - src/OpenBlade.Service/CpuLiveCurveOwnershipJournalStore.cs
  - tests/OpenBlade.Service.Tests/CpuLiveCurveOwnershipJournalStoreTests.cs
---

## Decided
Explicit CPU Refresh may settle a well-formed PossibleSubmission, OwnedVerified or Blocked live curve journal through the existing post-boot read-only transaction. Eligibility is not write authority: require a changed trusted kernel boot, a different known Service instance, drained workers, fresh exact-device/native admission, two complete exact-original curve vectors, unchanged power peers and no power ownership journal. Archive exact old bytes and the pre-settlement receipt before final admission/CAS. Preserve original context and transaction identity in the settled journal.
## Tried and ruled out
Do not rebind a live journal after power, sleep or Service continuity loss. Same-boot OwnedVerified still takes ordinary observation and may persist Blocked on context mismatch; Blocked cannot settle on that boot. Do not clear liveUncertainty to reach reconciliation, accept malformed/missing lifetime, infer zero/default originals, submit restoration, or claim that a prior failed cycle succeeded. A fresh post-boot observation proves only that the exact originals are now observed.
## Watch out
A fixture expecting zero reads and unchanged bytes for same-boot OwnedVerified across a different Service instance was incorrect: existing ordinary observation must persist its continuity block. The regression now follows that persisted block through same-boot refusal and a new boot/new controller into archived read-only settlement. Synthetic tests do not resolve the original RZ09-0528 owner's private cleanup receipts or historical PPT obligation. The previous learning's excluded OwnedVerified/Blocked limitation is superseded by this reviewed bounded eligibility extension, not a relaxation of native write admission.
