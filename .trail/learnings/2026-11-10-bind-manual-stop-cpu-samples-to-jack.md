---
title: Bind manual Stop CPU samples to the worker and attempt
author: Jack
date: 2026-11-10
branch: feat/rust-phase-5p-fan-stop-replay
touches:
  - docs/manual-fan-stop-replay.md
  - tools/openblade-windows-host/src/cpu_worker.rs
  - tools/openblade-windows-host/src/cpu_worker/windows.rs
  - tools/openblade-windows-host/src/fan_stop_entry.rs
  - tools/openblade-windows-host/src/fan_stop_entry/tests.rs
  - tools/openblade-windows-host/src/fan_stop_entry/tests/worker.rs
  - tools/openblade-windows-host/src/fan_stop_entry/worker.rs
---

## Decided
- Manual SO6120 entry replay uses the same context and thermal checks with either the historical corpus inputs or typed private-worker evidence. Worker values belong to the original attempt, worker generation and volatile observation. Keep request age and whole-proof age separate.
- A GPU reading has its own timestamp and observation. Preserve the difference between explicit GPU inactivity and explicit D3.

## Tried and ruled out
- Ready is insufficient. A delayed, mismatched, canceled or expired sample cannot complete a proof. A worker with missing exit observation cannot back a fresh verdict.

## Watch out
- Use the managed sampler and shared thermal corpus as the oracle. A scripted verdict and inert fixture process demonstrate software boundaries only. Installed and physical admission and exact external review are separate gates.
