---
title: Stream retained managed runs and validate snapshots before projection repair
author: Jack
date: 2026-11-12
branch: fix/history-streaming-admission
touches:
  - src/OpenBlade.Deployment/ManagedMailboxHistory.cs
  - src/OpenBlade.Deployment/ManagedMailboxRecovery.cs
  - tests/OpenBlade.Deployment.Tests/ManagedMailboxStreamingAdmissionTests.cs
---

## Decided

Retained managed runs have no cumulative lifetime count limit. Stream every additive entry through the production per-record parser/read path, keep only one record's inputs plus the separately bounded transfer snapshot and request lookup, and refuse late malformed, unbound, unmatched or legacy-disagreeing evidence. Total historical I/O remains linear, not constant latency. Reuse the validated view during Create under its existing writer instead of rescanning all runs after immutable publication.

Read the complete bounded transfer chain, existing deterministic projections, predecessor terminals, handoff/additive namespaces and current pointer before planned metadata repair. Genuine interrupted Create after durable immutable publication still repairs only the exact selection/request/run/current projections. No receipts, original exit, native proof or terminal authority can be invented by that repair.

## Tried and ruled out

Raising 8192, pruning old history, unauthenticated skipping and inventing retirement/index authority do not resolve the admission regression. Repairing early projections while later known-invalid evidence is still unread is not acceptable. Multi-file repair is not atomic: individual exact-or-publish checks preserve conflicts and IO failures can leave a truthful partial projection, which a later valid admission can finish.

## Watch out

Keep the reserved native projection independent of coherent managed substitution, preserve flat names accepted by the pinned predecessor, and retain legacy/new agreement, exact original build authentication and private enrollment/storage guards. The full Windows shared Rust backend goal, including Custom, long-running reads, mutations and remaining Service/Agent/background/config/model/journal/cutover work, remains deferred after this correction. This stage uses offline injected/private filesystem and inert static PE units only, with own SID/private ACL/file-sharing APIs; no product roots, role launches or runtime/hardware proof.
