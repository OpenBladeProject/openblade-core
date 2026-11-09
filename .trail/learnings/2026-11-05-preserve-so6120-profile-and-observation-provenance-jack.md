---
title: Preserve SO6120 profile and observation provenance through Auto exit
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-host-mailbox-contract.md
  - src/OpenBlade.Capture/So6120HostMailboxTransaction.cs
  - tests/OpenBlade.Capture.Tests/So6120HostMailboxTransactionTests.cs
  - src/OpenBlade.Capture/So6120DirectDutyCoordinator.cs
  - tests/OpenBlade.Capture.Tests/So6120DirectDutyCoordinatorTests.cs
---

## Decided

The SO6120 host-mailbox entry and Auto handback use their own typed coordinator. Capture an admitted normal profile in Automatic; stage B8/B9, set only F0 bit6, and publish FE last. Exit restores the captured normal selector directly, confirms Manual00, then restores only changed owned override and staged bytes. No forced Silent/Balanced destination, MCU gate/bank or paired HID Auto operation belongs to this backend. The initial experiment may independently restrict entry to Silent.

Every accepted observation advances an operation-wide sequence/time watermark, including polling after the last durable write checkpoint. Exit must reject any replay of those entry observations. Keep confirmed partial-entry cleanup separate from uncertain commands or journals; uncertainty fences all commands, including A0, and a failed durable fence remains explicit.

## Tried and ruled out

The MCU coordinator's ExclusiveAndSettled and gate/bank proofs cannot be fabricated for the host path. A successful FE write or matching PWM sample does not prove mechanical control. Idle policy command0A does not establish drained callbacks; every mapped A1 queues event14, and same-profile reload can restore a saved manual flag. Do not issue extra policy-command5 or a redundant Manual00 write to hide that uncertainty.

## Watch out

Read-only index06 maps EC active state802E3; bit0 gates FE. Require active observation, nonzero cycle, admitted counts and initial F0 bits5/6clear. Cycle limits apply to active requests, while saved inactive staging bytes may need exact restoration above that limit after observed Auto. F0 bit6 also affects a gated policy contributor, so exposure starts at possible permission publication, before FE. These one-shot coordinators remain offline: real durable storage, acquisition, accepted caller conditions, attended recovery and live rearming remain unbound. Snapshot reads and publication are not atomic exclusion.
