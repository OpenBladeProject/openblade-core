---
title: Keep guardian thermal sampling independent of native reads
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - build/CiImpactManifest.json
  - docs/so6120-stop-auto-guardian.md
  - src/OpenBlade.FanStop.Guardian/Program.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceAdmissionPolicies.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceDutyPlatform.cs
  - src/OpenBlade.Setup/PawnIoPrerequisite.cs
  - src/OpenBlade.Tray/MainWindow.FanStopSettings.cs
  - tests/Build.Tests/CiBuildScope.Tests.ps1
  - tests/Build.Tests/So6120CompletedAttemptArchive.Tests.ps1
  - tests/OpenBlade.Service.Tests/FanStopContinuationPolicyTests.cs
---

## Decided
Give the SO6120 guardian an independent OS-only safety sampler. It exclusively owns the temperature source and rolling policy, and publishes immutable timestamped proofs consumed by the serialized native owner. Keep the two-second acquisition, proof-age and genuine-gap limits. Start owned rolling history with a fresh observation; entry and command checks receive no elapsed credit. Preserve the first terminal reason and its thermal values through Auto restoration.

## Tried and ruled out
The installed thermal-exit trial showed a 2.89-second native coast read starving same-worker sampling. Moving sensors to a separate worker fixes that coupling; extending freshness or resetting history would hide the gap. A one-second delay after each completed sample also caused avoidable expiry for valid nonzero-duration reads. Schedule from sample start instead. Do not dispose a source until its sole outstanding operation actually completes, even after cancellation or timeout.

## Watch out
A final tach-zero observation is historical evidence, not permission to publish held Stop after admission has ended. Recheck the sampled proof after slow coast, power and transition reads. Keep same-owner Auto cleanup independent of the sensor worker. The old frozen test runner broke on a transient Inactive snapshot before released Auto; a new runner must retain the final reason during later read-only reconciliation without replaying writes. Native ambiguity remains fenced. Software coverage does not by itself establish installed thermal-exit validation.
