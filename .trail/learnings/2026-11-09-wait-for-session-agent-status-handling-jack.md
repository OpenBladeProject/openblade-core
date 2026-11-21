---
title: Wait for Session Agent status handling before asserting input behavior
author: Jack
date: 2026-11-09
branch: fix/session-agent-test-sync
touches:
  - tests/OpenBlade.SessionAgent.Tests/ServiceReactiveLightingLoopHostFunctionRowTests.cs
  - tests/OpenBlade.SessionAgent.Tests/ServiceReactiveLightingLoopTests.cs
---

## Decided

Use the fake watcher's DeliveryCount to wait for status handling in the Blade 18 host function-row and reactive source recovery tests. The watcher increments it after yielding the update, when the consumer requests the next one. In the recording factory, publish Last before incrementing the count and use a volatile read of the count.

## Tried and ruled out

Run 42702353120, jobs 129450518071 and 129471278894, recorded a null primary mode and a pulse timeout in separate attempts. With a temporary gate after admission publication, admission was true while the mode was null. With Reactive status held back until after replacement, pulse 53 was dropped and pulse 54 arrived after status handling. Both probes passed and their instrumentation was removed.

## Watch out

RunAsync creates the initial input source before handling any status. Factory creation does not mean reactive delivery is enabled. Blade 18 admission and primary mode are separate observations during a status update. A test that requires both should wait until the update is handled.
