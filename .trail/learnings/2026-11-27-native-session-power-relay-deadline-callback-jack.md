---
title: "Native session power relay deadline, callback slot and generation guard"
author: Jack
date: 2026-11-27
branch: feat/native-agent-power-automation
---

## Decided
- The native session power relay bounds each report with one deadline: `Gate::admit` returns `Instant::now() + remaining`, and `ReportChannel::exchange_on` runs the write and the reply read together under it. Expiry raises a typed `DeadlinePassed` error that `is_abandoned` maps to `Notice::Abandoned`, not `Failed { TimedOut }`.
- The transport send is `exchange_report(channel, instance, gate, connect, peer)`; production passes `connect_installed` and `installed_peer`. Tests drive `serve` through it over a real `ReportChannel` on a tokio `duplex`, which is what kills mutants that bypass or pre-take the gate.
- A report ends in the relay (`finish` or `dispose`) before its notice is recorded, on every path including stop. The test rig asserts `relay.lock().complete(sequence)` inside `record`, so every test pins that order.
- The Windows callback context is a leaked `Mutex<Option<Arc<Relay>>>` slot. The callback clones the relay out and works on its clone; a successful unregister empties the slot; a failed unregister leaves it. powrprof.dll is forgotten (never freed) after a successful registration.
- `Held` returns the channel generation on drop unless a peer verification failed, so a dropped report future keeps it.

## Tried and ruled out
- An in-flight callback counter with a drain wait in Drop: it relied on unregistration draining callbacks already dispatched, which Windows does not document.

## Watch out
- In `Wired` tests the Service end of the duplex must be held until the exchange ends, or a silent Service reads as end of stream instead of a timeout.
- The relay test clock (QPC `AtomicI64`) is independent of tokio's paused clock; advance both when a test spans verification time and reply time.
