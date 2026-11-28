---
title: Seed installed status before observation fixtures
author: Jack
date: 2026-11-12
branch: feat/installed-thermal-mutation
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
---

## Decided
An installed dispatcher fixture that bypasses full prepare must perform the same typed GetStatus initialization before its first ObserveState. The production readiness probe seeds the status publisher before returning readiness. Assert successful partial Custom observation and positive read-port creation before the first mutation, so later retirement assertions are not vacuous.

## Tried and ruled out
The 3c3128df run passed 38 of 39 Rust cases; the direct-pair case failed before its setter because its first request was ObserveState against an uninitialized publisher. That unavailable reply was correct for the fixture's missing startup phase. Do not weaken production admission, replace the actual Custom frame, fabricate levels or filter the failure out of the managed bridge.

## Watch out
Preserve the failed run and used private fixture files as intermediate evidence. The fix changes only fixture initialization and stronger assertions; the production probe, reader and mutation gates remain unchanged. Rebind the complete body and harness before retrying.
