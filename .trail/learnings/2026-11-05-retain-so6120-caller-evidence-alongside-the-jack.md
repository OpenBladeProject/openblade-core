---
title: Retain SO6120 caller evidence alongside the command journal
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Capture/So6120DirectDutySession.cs
  - docs/so6120-host-mailbox-contract.md
---

## Decided

The session separately binds caller-risk acceptance and hardware-test consent fields. The command journal retains its existing operation, device, firmware and consent binding; challenge decision, scope and evidence are pinned in memory by the session. A live runner must retain the reviewed caller evidence alongside its run.

## Tried and ruled out

The earlier session-composition learning's phrase "separate records" describes separate typed input roles. It does not establish two newly created durable approval artifacts. Neither role is authenticated by comparing fields, and journal inspection cannot prove caller-risk acceptance by itself.

## Watch out

Do not require arbitrary inequality between the two IDs or digests as a substitute for verification. Changing either bound role must be rejected independently. Native acquisition and test authority remain unimplemented.
