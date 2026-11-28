---
title: "Native GPU request time is not managed's token"
author: Jack
date: 2026-11-24
branch: feat/native-gpu-idle-d0
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/gpu_temperature.rs
  - tools/openblade-windows-host/src/windows/scm/installed/gpu_temperature_tests.rs
  - docs/windows-rust-development-service.md
---

Supersedes the `cancelled` half of "Native idle GPU reading that outlasts its observation's wait": the request's `until` is no longer part of managed's token.

## Decided
- On the live-readings path managed's token is only the connection or service token (`ControlRequestHandler.cs:266-270`, then `runtime.ObserveAsync`), never the observation's deadline. So `Cancellation::cancelled` is Stop alone, and the request's `until` (deadline minus `RESERVE`) sits in `unwanted` beside the dropped reply: it stops a sample only before its NVML read. An idle read that ends past `until` still keeps `last_on`.
- The worker sends no reply from a sample that ended past `until` (`Request::expired`). That is the hard bound on publication, independent of when the observation awaits its `Pending`.

## Watch out
- `tokio::time::timeout_at` polls the inner future before checking an already-elapsed deadline. A `Pending` awaited after `until` therefore returns a value the worker sent late, which is why the guard has to be on the worker side. `an_idle_read_past_its_requests_time_is_kept_but_not_published` fails with `Some(52)` without that guard, and with two NVML sessions if `until` cancels after the read.
