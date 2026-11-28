---
title: Keep Rust CPU worker readiness and exit evidence separate
author: Jack
date: 2026-11-10
branch: feat/rust-cpu-worker-contract
touches:
  - tools/openblade-windows-host/src/cpu_worker.rs
  - tools/openblade-windows-host/src/cpu_worker/protocol.rs
  - tools/openblade-windows-host/src/cpu_worker/tests.rs
---

## Decided
- In the Phase 5k development host, readiness and samples share the managed private wire sequence across worker generations. The supervisor's sample age starts before sending the sample request and never comes from readiness or a previous reading.
- Timeout or cancellation after sampling starts retires that stream. Cooldown and confirmed worker exit independently gate replacement. Stream EOF and the short stop deadline do not prove exit.

## Tried and ruled out
- PR #936 covers standard performance and ordinary fans but has no temperature admission seam; the existing temperature status fixture is not an admission input. Keep the CPU worker replay independent.

## Watch out
- A caller timeout during readiness does not cancel owned startup. The in-memory adapter does not establish protected launch, Job Object ownership, native exit observation, sensor availability, or installed thermal admission.
