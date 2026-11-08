---
title: Distinguish clean power query refusal from native ambiguity
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-stop-auto-guardian.md
  - src/OpenBlade.FanStop.Core/FanStopThermalPolicy.cs
  - src/OpenBlade.FanStop.Core/So6120OwnedStopSession.cs
  - src/OpenBlade.FanStop.Guardian/Program.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceAdmissionPolicies.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceDutyPlatform.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceSafetySampler.cs
  - src/OpenBlade.Tray/MainWindow.FanStopSettings.cs
  - tests/OpenBlade.Capture.Tests/FanStopThermalPolicyTests.cs
  - tests/OpenBlade.Capture.Tests/So6120OwnedStopSessionTests.cs
---

## Decided
Treat a conclusively refused adapter query as not submitted: end Stop admission and let the existing same owner attempt freshly admitted Auto cleanup. A persistent competing controller or lost ownership still forbids that command. Refresh typed power between coast observations and before publishing held Stopped; reuse the mandatory coast observation as the lower-power ownership proof. Anchor held polling to the real query timestamp.

## Tried and ruled out
Do not turn an entered-query error into cleanup authority or retry an uncertain query. Do not move query timestamps to completion or accept a thermal proof older than two seconds merely because its replacement is still pending. Individually valid slow reads can still leave actual uncovered time; the bounded owned-start handoff may therefore refuse and return to Auto. Keep those conservative bounds. Use the injected clock for sampler deadlines and propagate initial caller cancellation.

## Watch out
Record a completed tach-zero coast observation before the fallible post-coast power check, so a later recovery state retains physical-readback history without claiming that Stop remains held. Command admission now consumes immutable sampler proofs; remove the obsolete per-command policy mutation hook. Preserve semantic UI row assertions when merging new cards instead of treating the old total number of subcards as a contract. The Claude review returned ten findings but inspected recent guardian-related files, not the entire broad PR; record that coverage limitation.
