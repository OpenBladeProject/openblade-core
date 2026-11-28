---
title: validate present intent candidates before metadata repair
author: Jack
date: 2026-11-12
branch: fix/rust-intent-candidate-validation
touches:
  - src/OpenBlade.Deployment/ManagedMailboxRecovery.cs
  - tests/OpenBlade.Deployment.Tests/ManagedMailboxIntentAdmissionTests.cs
---

## Decided
Validate a present latest terminal-intent before deterministic projection/current repair even if terminal is absent. Share structural terminal-record checks against immutable selection, receipt/exit, mapping and retained proof; completed terminal still requires exact intent equality. Later fresh original/cache/native authentication remains independent.

## Tried and ruled out
Checking only completed terminals leaves corruption or partial-restore snapshots able to acquire metadata before refusal. Requiring terminal presence would deny the genuine proof/intent publication cut. Neither missing projections nor snapshot validation authorizes fabrication of proof, exit, Retired or replay.

## Watch out
Keep active/no-candidate and real after-intent publication cuts recoverable, including the distinct reserved mapping branch. Private negative cases must preserve exact names and bytes through both Selected and recovery. Retain bounded streaming, same-writer scan reuse, fresh Begin admission, legacy compatibility and private resolver guards. This is offline source/private-fixture evidence, with no installed cutover or hardware authority. Lexical matches about missing projections and shared validation are intentional: absent deterministic metadata is repairable only after the complete retained snapshot passes.
