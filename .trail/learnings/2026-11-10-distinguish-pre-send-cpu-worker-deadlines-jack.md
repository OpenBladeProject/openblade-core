---
title: Distinguish pre-send CPU worker deadlines
author: Jack
date: 2026-11-10
branch: feat/rust-cpu-worker-contract
touches:
  - "tools/openblade-windows-host/src/cpu_worker.rs#exchange_by"
  - tools/openblade-windows-host/src/cpu_worker/tests.rs
---

## Decided
- Distinguish an expired caller deadline before the first write from a timeout during a CPU worker exchange. Keep the healthy stream and unused sequence in the former case. Age an accepted sample from the first write attempt.
- Expose lost exit observation while the worker is still idle. Confirmed normal idle exit and the cold-sampling defer flag follow the managed reader.

## Tried and ruled out
- Treating every boundary timeout as possible submission could poison a ready worker even when no write was attempted. Comparing deadlines again after framing could also relabel the actual I/O outcome.

## Watch out
- A byte stream or stop signal still cannot prove process exit. These checks use inert streams and carry no authority for native launch or installed thermal control.
