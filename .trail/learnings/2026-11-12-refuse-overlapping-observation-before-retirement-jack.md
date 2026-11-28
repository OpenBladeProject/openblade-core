---
title: Refuse overlapping observation before waiting for retirement
author: Jack
date: 2026-11-12
branch: feat/installed-thermal-mutation
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
---

## Decided
Use nonwaiting operation admission for installed observations. A busy read or mutation returns unavailable before entering the existing native worker. Mutation keeps serialization and actual transport retirement. Bound the overlapping-read regression assertion while preserving all cancellation, callback-retirement and custody checks.

## Tried and ruled out
Adding an awaited operation mutex around the whole observation bypasses the worker's immediate occupied refusal. The GPU pending-submit fixture holds the first observation, then awaits a second observation before releasing the first; waiting for that same guard deadlocks and the request deadline never gets checked. A larger timeout or weaker retirement assertion does not correct this cycle.

## Watch out
The e605f16e grouped run stopped after nineteen completed cases before the held-GPU observation case could finish. Keep its original harness/log/source as failed intermediate evidence. This diagnosis and fix concern fake-I/O concurrency; they establish no hardware result. Rebind the corrected complete test body and harness before rerunning.
