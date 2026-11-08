---
title: Retain partial Stop refusal through verified Auto cleanup
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - docs/so6120-stop-auto-guardian.md
  - src/OpenBlade.FanStop.Guardian/Program.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceAdmissionPolicies.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceDutyPlatform.cs
  - src/OpenBlade.Service/FanStopController.cs
  - tests/OpenBlade.Service.Tests/GuardianFanStopBackendTests.cs
  - tests/OpenBlade.Service.Tests/FanStopGuardianProgressTests.cs
---

## Decided
The installed e820aed2 Custom65 run enabled the override permission but refused FE before Transfer began. A read-only export verified the journal's659-record hash chain: no FE publication, no PWM-zero or coast phase, then confirmed override restoration and completion. The fresh entry predicate was not retained; do not identify thermal or cached-publication freshness as this run's cause.

Preserve the first incomplete entry/coast reason through both ReturningToAutomatic and Completed. StopConfirmed means observed stopped tachometers and remains sticky through later drift and cleanup. A Stop request without that history returns Blocked even when Auto cleanup succeeds. Explicit Auto retains its independent release proof; a failed Auto result must propagate even if Stop was confirmed earlier.

## Tried and ruled out
The generic Auto completion message erased the partial-entry refusal. Applied Stop receipts and zero PWM alone do not prove stopped fans. The two-second cached-publication timing concern was ruled out for this recorded attempt because FE never reached Transfer. Do not fix a guessed cause by relaxing thermal or freshness admission.

## Watch out
Read protected run evidence using a bounded read-only export. Include the binary .journal as well as owner-result.json; the latter cannot reconstruct the failed predicate. Known-unsubmitted entry is distinct from uncertain submission, and a valid cleanup checkpoint does not establish successful Stop. Hardware verification of the corrected reporting needs its own authorized supervised trial.
