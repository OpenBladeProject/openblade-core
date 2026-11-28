---
title: Fence service run-marker writes in the scratch machine root
author: Jack
date: 2026-11-10
branch: feat/rust-phase-8h-run-marker
touches:
  - src/OpenBlade.Service/ServiceRunMarkerStore.cs
  - tests/OpenBlade.Service.Tests/ServiceRunMarkerWriterLeaseTests.cs
---

## Decided
The opt-in service run-marker store holds the enrolled machine writer lease through each disk operation, including atomic temp writes and compare-and-delete. Its optional v2 run ID distinguishes stores with identical clocks. The default v2 writer omits the field. An explicit transition independently admits the outgoing generation's retirement and requires clear marker and temp evidence under the lease.

## Tried and ruled out
Reacquiring the lease cannot establish exit or retirement. Clearing by path lets stale shutdown delete a successor marker. Reusing a leftover temp can erase evidence of an interrupted write. A failed begin, including a lease-release error, grants no cleanup authority.

## Watch out
Same-generation startup retains unexpected-exit classification. A store supports one outstanding run. The scratch coordinator must select explicit transition mode and supply a fresh generation-bound durable receipt with independent stop, exit, and recovery observations. The run ID only identifies marker ownership. Trail check's phrase matches concern unrelated status, automation, telemetry, and schema lessons. The opt-in exceptions deliberately deny mutation on uncertainty. The shutdown caller already logs cleanup failures.
