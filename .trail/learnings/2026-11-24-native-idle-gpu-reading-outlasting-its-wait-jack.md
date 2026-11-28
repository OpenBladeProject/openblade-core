---
title: "Native idle GPU reading that outlasts its observation's wait"
author: Jack
date: 2026-11-24
branch: feat/native-gpu-idle-d0
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/gpu_temperature.rs
  - tools/openblade-windows-host/src/windows/scm/installed/gpu_temperature_tests.rs
  - tools/openblade-windows-host/tests/data/gpu-temperature-vectors.json
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.NativeGpuTemperatureVectors.cs
---

## Decided
- Native has two stop signals where managed has one. `Cancellation::cancelled` is managed's token (Stop, or the request's `until`, which is `RESERVE` before the observation's deadline) and stops a sample after any step. `unwanted` (the reply was dropped, at `AFTER_READING` or because the observation went) is native-only and stops a sample only before the NVML read. After the read the sample rechecks the D-state and keeps a valid `last_on`, and the worker's `reply.send` just fails.
- Treating "unwanted" as cancellation after the read reopened the polling 8331bbf4 rejected: once the dwell was met, every later idle D0 sample did a cold `nvmlInit`/read/`nvmlShutdown` and published nothing.
- The managed vectors' `cancel` maps to `cancelled` alone, so no managed row changed; a closure is a `Cancellation` that is never unwanted (test-only blanket impl).
- `Reader::Ordinary` (managed `ReadAsync`) is `#[cfg(test)]`: only the vector replay uses it.
- Sampler rows may carry `readMs`, which advances the clock during the session's read in both the managed recorder and the native replay (`Script::stall`).

## Tried and ruled out
- Mutants: unwanted treated as cancelled after the read (killed by the worker test and `an_unwanted_sample_stops_before_the_nvml_read_and_keeps_one_it_made`), the cancel check before observing a non-D0 recheck, dropping `last_on` on a failed or implausible read, and stamping the reading before the read (each killed only by its new managed vector row), and ordinary-reader reuse.

## Watch out
- Perl `s|...|...|` with `\|\|` in the pattern is regex alternation, not a literal `||`: it matched empty at offset 0 and prepended text to line 1. Use `#` as the delimiter for Rust code.
