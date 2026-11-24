---
title: "Cooling pad lighting status after OpenBlade's own Apply"
author: Jack
date: 2026-11-22
branch: fix/cooling-pad-restart-resume
cost: { minutes: 10, tokens: 151134 }
touches:
  - docs/cooling-pad.md
  - src/OpenBlade.Protocol/SourceBackedCoolingPadDevice.cs
  - tests/OpenBlade.Protocol.Tests/CoolingPadSourceProtocolTests.cs
---

## Decided
- An OpenBlade pad Apply writes only profile 1 (VARSTORE) for built-in effects, so the two 0F/02 records usually differ afterwards. On the RZ09-0581 with an RC21-0230, the first read after an Apply on the "both records must agree" rule reported lighting unconfirmed.
- SourceBackedCoolingPadDevice now keeps lastConfirmedEffect, the effect this device session wrote last and confirmed: profile 1 for Apply, profile 0 for keyboard sync, and the active baseline (else the stored one) for restoration. ReadCore reports it while that record still holds it (ConfirmsEffect against the matching record); otherwise both records must agree.
- It is cleared before every lighting write and at a controller or identity boundary, and a new device session (service restart) starts unknown until its next lighting write.

## Tried and ruled out
- Reinstalling the identical build through the broker rolls back without touching the service, because the version is derived from the commit. To exercise a service restart, install a new commit or restart the service elevated.

## Watch out
- An external NOSTORE write to the other record while OpenBlade's record is unchanged would still be reported as OpenBlade's effect. Competing-controller detection is the guard; no read can tell.
