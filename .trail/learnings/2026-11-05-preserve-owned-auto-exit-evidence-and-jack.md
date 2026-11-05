---
title: Preserve owned Auto exit evidence and reconcile unsubmitted attempts
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Capture/DirectDutyAutomaticExit.cs
  - src/OpenBlade.Capture/DirectDutyUnsubmittedReconciliation.cs
  - tests/OpenBlade.Capture.Tests/DirectDutyAutomaticExitTests.cs
  - tests/OpenBlade.Capture.Tests/DirectDutyUnsubmittedReconciliationTests.cs
  - docs/so6120-direct-pwm-handback.md
---

## Decided

Resolve the five implementation issues and three offline contract gaps from the Claude review of bf3f3a2b. Generic Auto retains captured or intended admitted performance and still requires both commands with settled independent readback. DirectDutyCustomState contains hardware fields only; legacy GPU offsets stay out of comparison. Read retained Custom state independently before any mutation and refuse missing data, requested level deltas or an active maximum-fan flag because Auto has no hidden level/max-fan setters.

Post-ack readback rejection is Uncertain with its underlying Cause preserved. Validate captured manual flags before claim acquisition. Track acquisition provenance explicitly; release failures are separate from operation failure and preserve historical fan handback and hardware cleanup while preventing overall success. A second-channel confirmation-journal failure preserves the independently observed handback, not cleanup success.

## Tried and ruled out

Do not require a fresh bank read through a closed gate. Read bank immediately before closure, bind that proof into the durable gate intent/confirmation, then use a dedicated gate-only proof with Bank=null. This depends on pinned bank-preserving gate-close semantics and continuous exclusion; it does not excuse an unknown transport. Do not add a default-profile round trip, skip the second Auto channel, hide multiple hardware writes in a step or replay an ambiguous submission.

## Watch out

DirectDutyUnsubmittedReconciliation is explicit and performs no hardware writes. It requires complete durable DefinitelyUnsubmitted evidence, positive prior-session release, fresh ordered owned-state observations and an atomic prior-attempt/generation check. Fence the old attempt before authorizing one new attempt. Every native submission must recheck the fence and durably mark its boundary under the same exclusion. Matching hardware, zero local counters, missing journal records or uncertain intents alone never authorize reuse.

Capture tests passed 611/611, including 78 cases across exit/reconciliation, and independent integrated review passed. Tests use a simulated in-memory journal, not persistent-storage or hardware evidence. Real adapter durability, exact-model admission, exclusion and physical handback remain unimplemented/unverified; no live route or production decoder change was introduced. The previous review learning records the historical open findings at bf3f3a2b; this fix supersedes those unresolved statuses without editing that learning. Trail lexical overlaps repeat rejected bypasses and preserve exact-device and stop-on-ambiguity boundaries; they do not grant hardware authority.
