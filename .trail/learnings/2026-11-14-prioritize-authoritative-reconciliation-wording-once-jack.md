---
title: Prioritize authoritative reconciliation wording once
author: Jack
date: 2026-11-14
branch: fix/native-tfc-outcome-publication
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/temporary_charge_release.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
---

## Decided
An unresolved settlement uses its authoritative pending reason alone in the public battery diagnostic, while preserving the exact SID-owned request outcome separately. The existing typed equality renderer then emits the same pending reason once. Busy and tagged read-unavailable release pauses still compose separately with operation/observation messages.

## Tried and ruled out
The first frozen23f09140 correction appended a generic release prefix to an already authoritative pending public reason. Actual frame55 then repeated that reason because the public and owned strings were no longer equal. Keep that actual evidence; do not widen rendering to substring deduplication or change ownership. Immediate and stale-proof failed-settlement assertions now require one occurrence.

## Watch out
A reconciliation pause is still a typed release deferral; using the authoritative reason does not unpause writes, clear quarantine, alter original caller/deadline gates, re-age observations or replay commands. Trail lexical matches of equality, public/private messages and pending wording are affirmative contract preservation, not conflicts. Root still owns provider review and acceptance.
