---
title: Recheck SO6120 mailbox staging after publication admission
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-host-mailbox-contract.md
  - docs/so6120-mailbox-calibration.md
  - docs/so6120-mailbox-trigger-test-plan.md
  - tools/mailbox-observation/Export-MailboxTrace.ps1
  - tools/mailbox-observation/MailboxTraceController.cs
  - tools/mailbox-observation/MailboxTraceExporter.cs
  - tools/mailbox-observation/README.md
  - tools/mailbox-observation/Start-MailboxObservation.ps1
  - tools/mailbox-observation/analyze_observation.py
  - tools/mailbox-observation/tests/Test-MailboxTraceController.ps1
---

## Decided

The SO6120 developer mailbox core uses one bank-0, count-1 request, clears the completion byte, stages and verifies the descriptor, and publishes A0/A1 last. Fresh publication admission can itself take time, so recheck descriptor, completion, idle command and staged payload after that callback. A confirmed A1 requires a separately admitted A0 readback with a new request identity. Retain possible publication across both transactions.

## Tried and ruled out

A passing native call is not handler completion, and a matching byte does not establish ownership when it already matched. Do not require host bank 90 to equal the unrelated SMBus bank 0F. Do not interpret the driver's write Information value of decimal 10 as a failed zero-output write; require native success. Reads require success and exactly one returned byte.

## Watch out

The core is developer-only, uses an injected fixed-port boundary and has no live admission or CLI. Outer code still owns durable intent, exact-device and lifecycle checks, field ownership, bit preservation and accepted contention conditions. A0 is a command publication, not passive reconciliation after ambiguity. Never retry or issue cleanup after uncertain publication. PolicyCommand and PolicyStatus are readable but not writable through this core. The independent 45-case fake-port suite checks mutation after admission, readback failures and every native-operation failure; it does not establish physical control.
