---
title: Persist SO6120 command intent without granting replay authority
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-host-mailbox-contract.md
  - src/OpenBlade.Capture/So6120DirectDutyJournal.cs
  - src/OpenBlade.Capture/So6120SupervisedAdmission.cs
  - tests/OpenBlade.Capture.Tests/So6120DirectDutyJournalTests.cs
  - tests/OpenBlade.Capture.Tests/So6120SupervisedAdmissionTests.cs
---

## Decided

Use the SO6120 developer journal for one agreed run root, with CreateNew, exclusive file ownership, bounded sequenced/hash-chained frames and Flush(true) before returning from each append. Bind the owner, model evidence, requested duties and consent context once. Record mapped reads as command transfers too; publication intent is durable before the byte-core publication, while actual results remain separate. A write or flush failure poisons the instance and never fabricates a successful durable fence.

## Tried and ruled out

A valid checksum does not authenticate hardware or consent. Do not reopen completed or interrupted files for writing. Post-release inspection never grants resume/recovery authority, and another directory is not excluded by this file lease. Review caught hash-valid JSON with missing/defaulted or duplicate fields and a claimed successful transfer lacking its final A0 payload read; strict parsing and same-request completion evidence now reject both.

## Watch out

The held FileShare.None stream deliberately prevents inspection of a live partial tail. Closing does not assert restoration. A valid final frame after interruption does not prove the prior caller received flush acknowledgement. Trusted run-root selection, actual hardware outcomes, admission and explicit recovery still belong to the outer owner. Tests exercise real temporary files, injected write/flush faults, corrupt records and the byte core through memory-only ports; none exercises hardware.
