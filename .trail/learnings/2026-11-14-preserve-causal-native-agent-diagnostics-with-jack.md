---
title: Preserve causal native Agent diagnostics with redacted fallback
author: Jack
date: 2026-11-14
branch: feat/native-agent-battery-report
touches:
  - rust/crates/openblade-platform-windows/src/agent_diagnostics.rs
  - tools/openblade-windows-host/src/installed_agent_main.rs
  - tools/openblade-windows-host/src/session_native.rs
  - tools/openblade-windows-host/src/session_native/battery.rs
  - tools/openblade-windows-host/src/session_native/battery/tests.rs
  - tools/openblade-windows-host/src/session_native/installed_monitor.rs
  - tools/openblade-windows-host/src/session_native/scm_peer.rs
  - tools/openblade-windows-host/src/session_native/service_client.rs
  - tools/openblade-windows-host/src/session_native/diagnostics.rs
---

## Decided
Native Agent terminal diagnostics carry a finite causal event with the original error, rather than inferring source from the outer I/O kind. Tag initial acquisition, all later Agent origin checks, and the nested Origin::committed verification used by the Service generation adapter. Preserve the cause through the actual Denied I/O conversion. Listener/claim failures remain distinct; peer/frame failures stay connection-local. Error kinds, authority decisions, wire contracts, cancellation and retirement sequencing do not change.

A retirement event already attempted at its causal boundary must not be relabelled Endpoint at terminal reporting. The attempt marker is diagnostic-only and never proof that either destination retained bytes. Preserve the original error in its source chain and bound the metadata walk.

A successful persistent record needs no stderr duplicate. If registration is absent or the event store fails, try one finite typed stderr line in a 512-byte buffer; omit persistence-error text, paths, identities, XML and arbitrary strings. A failed stderr write stays best-effort. This intentionally supersedes the earlier statement that unregistered record is wholly inert: persistence still performs no path access, while the redacted fallback remains available for rejected startup.

## Tried and ruled out
Do not attach a generic Endpoint label to every error leaving entry_installed, use a global last-error flag, treat a sink Registration as proof an event was stored, or suppress stderr merely because the selected feature compiled. Do not log raw errors to recover missing diagnostics. Keep original admission and store identity checks, with no alternative log root or real registration during injected tests.

## Watch out
The Service generation adapter and Origin::committed contain later origin checks too; fixing only battery dispatch misses them. Focused tests use the actual connection loop and actual Denied conversion plus injected fallback writers, never a product role, LOCALAPPDATA registration or powercfg. Successful byte-bounded attempts cannot guarantee visibility when both destinations are unavailable. No new managed frame or broad-suite claim is made when the wire/parser/store paths are unchanged. Preserve earlier seals and qualify new evidence separately.
