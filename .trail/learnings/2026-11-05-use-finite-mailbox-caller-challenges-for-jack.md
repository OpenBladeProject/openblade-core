---
title: Use finite mailbox caller challenges for scoped empirical risk acceptance
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-mailbox-trigger-test-plan.md
  - docs/so6120-mailbox-runtime-observation.md
---

## Decided

Use a finite caller-challenge plan to assess practical mailbox contention risk for an attended SO6120 Silent experiment. Agree on coverage, actions and residual-risk acceptance before collection; do not demand proof of all future absence or keep adding quiet repetitions. A valid clean result can support a scoped empirical decision without manufacturing a hardware lock, admitting production fan stop or authorizing raw writes. The design is in docs/so6120-mailbox-trigger-test-plan.md; it has not run.

Three candidate windows cover an existing non-storage, non-power-supplying Type-C peripheral round trip, AC power round trip, and one supported profile round trip. TBCC's TBT text and TYPC update make Type-C the strongest hypothesis, but no hot-plug-to-TBCC edge is established. _Q40 calls UFEC then RECR; _Q13 exercises power notifications; _Q30/_Q33 exercise thermal policy. Do not count unrelated AML as proof the target path ran.

## Tried and ruled out

The saved ETL has 1107 File creates, all unnamed, and 1105 File closes, 1103 named. A named close can identify a transient client missed by snapshots; an older parser comment saying names are always empty is not true for this captured build. Validate the raw payload and unambiguous lifetime, never join object pointers across reuse. None of the prior 947 blank File rundown rows gains a name from a same-rundown pointer match.

## Watch out

Verify held and transient ordinary-file controls plus relevant ACPI entry coverage. Root-only tracing may cover external TBCC calls if its emit paths and filters are established; incomplete nested coverage must stay explicit. An ordinary-file marker does not establish universal device-name coverage. Treat confirmed target activity as an identified possible client, collection defects as inconclusive, and unmeasured actors as explicitly accepted or unresolved risk; do not invent a probability from a few correlated challenges. Reserve both queued buffers and final rundown: a 128 MiB cap with up to 32 MiB queued plus 32 MiB rundown needs a 64 MiB on-disk early-stop watermark. Restore agreed baseline after an early trace stop while preserving hardware stop-on-ambiguity rules. No new trace, device open or firmware action occurred during design.
