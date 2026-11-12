---
title: Validate latest completed terminal before metadata repair
author: Jack
date: 2026-11-12
branch: fix/latest-terminal-admission
touches:
  - src/OpenBlade.Deployment/ManagedMailboxRecovery.cs
  - tests/OpenBlade.Deployment.Tests/ManagedMailboxTerminalAdmissionTests.cs
---

## Decided
Validate the latest managed mailbox transfer whenever terminal.bin exists before accepting a repair snapshot. Every predecessor still requires its terminal. ValidateTerminal derives its exact Terminal.v2 bytes from immutable selection, receipt, original exit, native proof and optional mapping, so missing deterministic selection/request/run/current projections need not block a valid completed terminal's repair. Require completed terminal-intent bytes to equal those exact terminal bytes; both current and pinned DFD writers publish intent before terminal. Keep ResolveOne's later independent original/cache/native authenticated comparison.

## Tried and ruled out
Hashing the latest terminal without validating it lets three invalid completed snapshots publish deterministic metadata before later refusal. Seven new private rows on the unchanged 0c7da2ec runtime reproduced all three negatives and passed all four positives. Terminal existence cannot be required for every latest transfer: a genuine active record or real cut after durable terminal intent is a supported interrupted prefix. Do not infer terminal, native retirement, proof, or original exit from intent or absent files.

## Watch out
Retain same-lock quiescence reuse and fresh Begin admission across writer gaps. Do not reseal old evidence or present the parent's scoped PASS as acceptance of this remaining R1 fix. Source and offline private tests establish metadata ordering only; full eventual review covers 99fdeb54 through the corrected final head, including 3b59 and 0c7d. A prior intermediate manifest is stale and remains qualified; its final sealed inventory plus separate supplement are the authoritative retained bytes. Missing projections in these private tests are controlled fault injection, never permission to delete real history.
