---
title: Bind three-channel Fan Stop workflow without live transport admission
author: Jack
date: 2026-11-09
branch: feat/fan-stop-model-expansion
touches:
  - docs/fan-stop-model-coverage.md
  - docs/fan-stop-reader-and-ec-followup.md
  - src/OpenBlade.FanStop.Core/So6120DirectDutyCoordinator.cs
  - src/OpenBlade.FanStop.Core/So6120DirectDutyJournal.cs
  - src/OpenBlade.FanStop.Core/So6120DirectDutySession.cs
  - src/OpenBlade.FanStop.Core/So6120HostMailboxTransaction.cs
  - src/OpenBlade.FanStop.Core/So6120OwnedStopSession.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceAdmissionPolicies.cs
  - tests/OpenBlade.Capture.Tests/So6120DirectDutyCoordinatorTests.cs
  - tests/OpenBlade.Capture.Tests/So6120DirectDutyJournalTests.cs
---

## Decided
Bind the three-channel workflow to an explicit operation shape and a separate v3 journal binding. Stage B8/B9/9C before the override and FE selector. Restore the captured selector and override before restoring changed staging fields. Require independent, in-range actual duty for all three channels and require zero tach on each before reporting stopped.

## Tried and ruled out
The public RZ09-0529 OpenRazer report includes scoped laptop and cooling-pad descriptors. That report cannot establish normal enumeration of the EC-native 1532:F003 endpoint.

## Watch out
V3 exercises simulated workflow and journal replay. The live session and guardian admit two fans only, and the native A0/A1 allowlists exclude 9C. Third-duty readback, writer ownership, completion and live handback still need exact-model evidence. Existing v1/v2 journals omit third-channel fields.
