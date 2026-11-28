---
title: Keep manual SO6120 entry evidence separate and expiring
author: Jack
date: 2026-11-10
branch: feat/rust-manual-fan-stop-replay
touches:
  - tools/openblade-windows-host/src/lib.rs
  - docs/manual-fan-stop-replay.md
  - tools/openblade-windows-host/src/fan_stop_entry.rs
  - tools/openblade-windows-host/src/fan_stop_entry/tests.rs
  - tools/openblade-windows-host/tests/oracles/manual_fan_stop/ManualFanStopEntryOracle.cs
  - tools/openblade-windows-host/tests/oracles/manual_fan_stop/manual-fan-stop-entry.tsv
  - tools/openblade-windows-host/tests/oracles/manual_fan_stop/manual-fan-stop-pins.txt
---

## Decided
Phase 5o replays new manual RZ09-0581 SO6120 EC 1.04 Stop entry as an inert Windows host library. PR #936 at 6b1de6d is the Rust base; public main 1d2689d is the managed reference only. Service Balanced readback and typed USB-C are separate from owner-bound volatile and temperature evidence.

## Tried and ruled out
Do not port the attended capture admission, Battery Saver, automatic entry, continuation, or Auto cleanup as manual entry. The managed continuation snapshot has placeholder power and thermal booleans. CPU reader readiness and a pending read are not safety proof.

## Watch out
Managed manual entry has a strict 45-second budget. The sampler proof expires two seconds from admission start; typed power is independently aged from read start. Earlier volatile refusal must not borrow a thermal reading. Only completed same-run evidence can be replayed at the cached gate. This is not native admission, service ownership, or physical parity.
