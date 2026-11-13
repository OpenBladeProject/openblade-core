---
title: Retain native fan owners across intent epoch and cancellation changes
author: Jack
date: 2026-11-13
branch: fix/windows-rust-fan-ownership
touches:
  - rust/crates/openblade-domain/src/fan_history.rs
  - rust/crates/openblade-domain/src/thermal_history.rs
  - rust/crates/openblade-domain/src/thermal_mutation.rs
  - src/OpenBlade.Deployment/WindowsNativeFanHistory.cs
  - src/OpenBlade.Deployment/WindowsNativeThermalHistory.cs
  - tests/OpenBlade.Deployment.Tests/NativeThermalHistoryTests.cs
  - tests/OpenBlade.Deployment.Tests/OpenBlade.Deployment.Tests.csproj
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/fan.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
---

## Decided
Retain positive native fan cleanup provenance independently of mutable saved policy. Release still checks configuration integrity, exact original custody/generation, caller scope, source/topology, controllers, current admission and original reserved IDs. A fully fresh explicit same-SID/session/authentication request may supersede the old lifecycle proof; implicit Stop may not renew it. Join pending native work before replacing its permit and adopt the latest worker-owned30 even if its outer future was canceled.

The linked-v2 fan-row contract adds progress51 only for independent already-Automatic observation tied to the exact original manual owner. It claims no setter ACK or new ownership; legacy linked-v1 bytes/progress retain their meanings. Ship compatible Rust and managed readers first; old readers must refuse new rows. Update all settled/reload/rollover/reservation/handback consumers together. Native callback/thread retirement remains distinct from firmware settlement.

Use shared runtime capture admission for fan, performance and retained policy. A foreign scope must not evict the sole original user Storage cache while a fan worker is registered/unjoined or its journal is pending/outstanding. An await guard releases only never-registered requests on cancellation/return; registered work waits for actual retirement. Clear stale outer ownership only after a joined worker and authoritative no-pending/no-outstanding history. Managed new-request-after30 joins owner, SID and session, while permitting a different PID across distinct requests. Unaccepted or ambiguous/fenced fan results are Blocked; accepted software-only pending intent stays Stored.

## Tried and ruled out
Reusing old ordinary authorization for cleanup stranded positive owners after refused saved changes, profile edits and temporary expiry. Requiring the old lifecycle proof before fresh capture stranded explicit Auto after normal epoch changes. Promoting only after the outer await lost a worker's owned30 on cancellation. Auto50/51 can complete before outer cancellation too, leaving a stale owner pointer unless durable completion clears it. Fan-only caller scope gates miss the shared performance/policy factory and its one-user cache. Looking only at the owned atomic misses the durable30-to-atomic publication window. Do not change Complete/Idle on the refuted transport-retirement naming argument.

## Watch out
Reproduce races with the actual worker retirement hook, not ordinary ready drain awaits. Use full Aowned30 -> B0/6 -> exact A31..50 parser vectors; an unsettled prefix alone cannot demonstrate managed public acceptance. New progress51 must never be admitted for legacy/nonfan records or used to fabricate failed setters. Preserve global ambiguity and one-shot Stop fences, saved intent and finite budgets. All validation for this correction is offline pure/injected/private-file evidence; real suppliers compile only, with no installed app changes or physical fan claims.
