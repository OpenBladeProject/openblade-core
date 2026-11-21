---
title: Stale power-only write block at a classified power change
author: Jack
date: 2026-11-21
branch: fix/stale-power-only-write-block
cost: { minutes: 13, tokens: 250238 }
touches:
  - Directory.Build.props
  - src/OpenBlade.Service/ServiceControlRuntime.UnclassifiedExternalPower.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.StalePowerOnlyBlock.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.StalePowerOnlyBlockProbe.cs
---

## Decided
- A classified power-source change (ChangePowerSourceCore, changed == true) releases a published block only when HoldsOnlyPowerWriteBlock holds: verified device, classified context, NonPowerClassificationWriteBlock false. Eligibility is computed before InvalidatePendingWriteConfirmations so a pending-held block (possibly submitted write) is never released here; PR #991's ReleaseWriteBlockHeldByPendingConfirmations owns that case.
- Clear rather than defer: deferring a guard-refused thermal step needs new continuation semantics (PR #991 round 4 lists it as a known limitation).
- RefreshingControls/CheckingConnection are labels, not owners. HasExplicitNonPowerWriteBlock = HasNonPowerWriteBlockOwner || (label && block not tracked power-only). The publish layer's carriesOnlyPowerBlock uses the owner set so a transient label neither starts nor ends power-only tracking.
- PublishCompletedUnresolvedPowerClassification adds the transient label to WritesBlocked (not to tracking), matching the periodic classifier, so the split does not unblock ordinary unclassified control during a missed read.

## Tried and ruled out
- Releasing on an unchanged classified source in ChangePowerSourceCore: allowCachedExternalClassification returns Automation.PowerSource without a fresh read while a conflict block is published (conflict sets externalClassificationUnavailable = 0). Only a changed source is fresh by construction.
- A "transient failure before the conflict" test variant: the power-change handler's own fresh read clears RefreshingControls first, so it collapses to the plain case.

## Watch out
- Repro: CreateWritableContext, Observe, telemetry ExternalUnknown + AdapterPowerStates FullPower280W then UsbCPower100W, HandlePowerSourceChangedAsync, then Battery. On main the thermal step is refused.
- git show with origin/branch:path in Git Bash needs MSYS_NO_PATHCONV=1.
