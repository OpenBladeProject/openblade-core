---
title: Retain Service generations and schedule from observation due time
author: Jack
date: 2026-11-11
branch: fix/rust-status-generation-cadence
touches:
  - tools/openblade-windows-host/src/session_native/service_client.rs
  - tools/openblade-windows-host/src/windows/scm/installed/subscription.rs
  - tools/openblade-windows-host/src/windows/scm/installed/status_source.rs
---

## Decided
Acquire an independently authorized, retained Service generation per connection. Verify that same generation around requests, responses and events, including same-PID replacements. Only trusted absence or retirement is retryable; a failed identity proof remains denied. Keep ObserveState's stronger authority requirement separate. Installed authority remains unwired.

Back off after short subscriptions as well as failed opens. Reset exponential delay only after sustained connection time; idle subscriptions need no heartbeat. Preserve replacement initial snapshots even when their revisions decrease.

Return the next observation deadline from the source that owns observation freshness, cache and revisions. The one demand-driven subscriber scheduler sleeps until that deadline, including when GetStatus moved it near a poll boundary. Unchanged observations do not create revisions.

## Tried and ruled out
A PID, pipe name or path cannot identify a generation. Successful SubscribeStatus alone is not stability. A second fixed two-second sleep after a skipped fresh-cache check stretches observation gaps. The old shutdown-deadlock claim was disproved: keep refresh scoped inside serve_connections, dropping before source drain.

## Watch out
Audit exact test bodies and run named duplex/in-memory/virtual-time cases only. Prove shutdown with refresh holding or queued for the source mutex. These tests do not establish Windows process-generation, kernel-peer, installed ownership or physical hardware authority.
