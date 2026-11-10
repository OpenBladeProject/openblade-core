---
title: Confirm scoped keyboard Off after the port getter
author: Jack
date: 2026-11-10
branch: test/rust-lighting-off
touches:
  - tools/openblade-windows-host/src/battery_runtime/lighting.rs
  - src/OpenBlade.Protocol/ProductionLightingPort.cs
  - src/OpenBlade.Service/ControlCoordinator.cs
---

## Decided
The first private Rust keyboard Off route is scoped to RZ09-0581 (1532:02E0) with no renderer producer. Stage the override separately from observed brightness. Use the port's brightness-zero recipe and the scoped final query. Keep logo evidence separate.

## Tried and ruled out
A setter echo and the port's zero sample cannot confirm the request when the final scoped getter is unavailable. Keep the fence until a later read-only zero. Trail can flag lexical overlap with prior battery, lighting and storage learnings even when the path preserves those constraints.

## Watch out
The fixture's persistent slot models intent lifetime within the process. Protected override persistence and an owned renderer need their own lifecycle work. An owned renderer must drain its producer before the Off submission and keep port retirement fenced.
