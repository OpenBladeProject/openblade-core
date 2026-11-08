---
title: Bind SO6120 commands in a one-owner offline session
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Capture/So6120DirectDutySession.cs
  - tests/OpenBlade.Capture.Tests/So6120DirectDutySessionTests.cs
  - src/OpenBlade.Capture/So6120DirectDutyCoordinator.cs
  - docs/so6120-host-mailbox-contract.md
---

## Decided

Compose the SO6120 coordinator, mailbox core, journal and admission policy in one serialized, injected-only session. Keep the original run, clock, console, journal identity and caller evidence bound across entry and explicit restoration. Caller-risk acceptance and hardware-test consent are separate records. Recheck fresh metadata before staging and each A0/A1 publication, including confirmation reads, and recheck age after durable publication-intent flush. A matching bundle is input validation, not authentication or hardware authority.

## Tried and ruled out

Do not let successive bundles pass by agreeing with themselves after their run or session changes. Do not issue nested mailbox reads from a staged request's admission callback. Do not interpret a software deadline as cancellation of synchronous kernel I/O or a thermal recovery guarantee. Journal inspection cannot restart this one-shot operation; disposal cannot silently hand back fans.

## Watch out

The thirteen mapped reads form a sequential observation, not an atomic snapshot or reservation. Admission loss, uncertain command results and journal failure fence later commands, including A0. Durable proof of no submission is required before the existing explicit partial-entry cleanup path can proceed. Native acquisition, trusted test consent and caller evidence, actual OpenBlade writer handoff, and attended exposure/recovery remain separate live-runner obligations. This supersedes older notes saying the offline boundary is wholly unimplemented; their hardware-authority and ownership exclusions still apply.
