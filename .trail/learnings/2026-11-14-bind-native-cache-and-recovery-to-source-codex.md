---
title: Bind shared native artifacts and recovery to confirmed source
author: Codex
date: 2026-11-14
branch: feat/all-external-power-modes
touches:
  - build/NativePayload.ps1
  - build/Build-NativeDevelopment.ps1
  - src/OpenBlade.Service/HyperBoostOwnershipJournalStore.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
---

A single Cargo target shared across worktrees can reuse a different path dependency when the second checkout has older timestamps. A real two-crate probe returned A's value for B despite different absolute Cargo package identities. Bind every crate to a stable source-content compiler flag, hold the common publication mutex through copy and provenance, and reject changed inputs. The fixed shared-cache round trip returned A=1, B=2, A=1; no separate target was needed.

A legacy RestorePending HyperBoost journal has no trustworthy destination-source provenance. Preserve its data but require current-destination rebasing before either a setter or no-write retirement. Retire an already matching exact intent only with confirmed matching source and no outstanding destination rebase. Unknown lifecycle power must preserve the exact owned record without constructing invalid recovery metadata or issuing speculative setters. Clearing boundary bookkeeping does not independently clear the global journal safety latch.
