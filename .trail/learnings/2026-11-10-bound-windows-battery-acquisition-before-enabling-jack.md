---
title: Bound Windows battery acquisition before enabling live collection
author: Jack
date: 2026-11-10
branch: feat/windows-rust-battery-acquisition
touches:
  - src/OpenBlade.SessionAgent/WindowsBatteryReportParser.cs
  - tools/openblade-windows-host/src/session_battery/acquisition.rs
  - tools/openblade-windows-host/src/session_battery/timestamp.rs
---

## Decided
The Rust Session Battery acquisition seam is compiled without a live caller. Inert leaf-child tests exercise a GUID scratch attempt, the 15-second ceiling, cancellation and bounded stop confirmation, a 4 MiB file limit and nonrecursive cleanup. The private pipe keeps its existing replay and the managed endpoint stays the default.

## Tried and ruled out
At cc0f0ebc5c7c9c7a3070a583fc20180bd588791a the managed parser returned charges 1 and 18 on the shared equal-start XML. Stable report order in both parsers selects 1 and 2. Shared timestamp cases pin the selected invariant forms and 100 ns rounding. This is a bounded grammar, not a clone of invariant DateTimeOffset.TryParse.

## Watch out
The pinned provider is Git blob 120e915d12a5a8f36d5c6d7405f2a87e998e738f, the baseline parser is 3ccd2e1e3c2037baf9c118ded27a691f098427bd, and the provider and parser tests are 83909c1f1828e065eccf54127351fb28b689e2ba and 7b8c73e77a16e646400c8230024877aa68c5bf93. Before any live wiring, independently establish actual report encoding and timestamp/usage forms, current-user launch and exit behavior, process-tree ownership and cancellation cleanup. If exit cannot be confirmed, retain the scratch attempt rather than delete underneath the child.
