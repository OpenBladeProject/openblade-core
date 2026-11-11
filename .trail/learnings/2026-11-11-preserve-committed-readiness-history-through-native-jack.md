---
title: Preserve committed readiness history through native metadata recovery
author: Jack
date: 2026-11-11
branch: fix/native-origin-status-corrections
touches:
  - src/OpenBlade.Deployment/WindowsNativeAttemptHistory.cs
  - tests/OpenBlade.Deployment.Tests/NativeRecoveryTests.cs
  - tools/openblade-windows-host/tests/data/native-origin-corrections-v1.json
---

## Decided
Recovery must retain committed-readiness history independently of the current metadata phase. Inspect and validate a preserved Committed.bin for the exact same epoch/origin/package/request/artifact before accepting a no-launch retirement proof, even when interrupted publication recovery changed current to Unresolved and zeroed readiness. Retained commit intent at phase/frontier/current crash boundaries cannot erase a missing-submission or missing-launch contradiction. The exact source verifier caught this gap in 9ec906bc; focused private-filesystem crash regressions now cover all three boundaries.

## Tried and ruled out
Checking only Record.Phase==Committed loses the historical declaration during metadata reconciliation. It is not safe to infer never-started solely from the new Unresolved label or an empty attempt directory. Complete validated launch/drain pairs and independent firmware-owner fences are unchanged.

## Watch out
Metadata phase normalization does not delete or supersede historical evidence. Preserve every historical record and refuse malformed/unreadable/mismatched committed history. This is offline source/private-filesystem proof, not live Windows or firmware validation.
