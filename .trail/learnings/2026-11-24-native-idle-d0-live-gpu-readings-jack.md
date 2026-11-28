---
title: "Native idle D0 live GPU readings (8331bbf4 port)"
author: Jack
date: 2026-11-24
branch: feat/native-gpu-idle-d0
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/gpu_temperature.rs
  - tools/openblade-windows-host/src/windows/scm/installed/gpu_temperature_tests.rs
  - tools/openblade-windows-host/tests/data/gpu-temperature-vectors.json
  - docs/windows-rust-development-service.md
---

Supersedes the `PENDING_PARITY` part of "Native protocol 90 and the pending-parity allowlist": all eight rows were ported and the allowlist and its test were removed.

## Decided
- `Sampler::read(live_readings, cancelled)` carries both managed readers; the Service calls only `sample`, the live one. The ordinary (`ReadAsync`) reader exists so the vectors that interleave both readers over one D0 state replay. It is not dead code: removing it breaks those rows.
- Renewal is `active && celsius.is_some()`, not any reading: idle reads and reuses end the session before the sample returns, as managed's `release` stays true for them.
- Power is observed after the D-state cancellation check, as managed throws before `ObserveGpuPower`. A sample cancelled there leaves the dwell and the reading; the vector "a live sample cancelled after its power read observes no power" pins it. A failed recheck observes Unknown before the next cancellation check.

## Tried and ruled out
- Twelve hand mutants (dwell 2999 ms and strict, interval inclusive and 10001 ms, no reset of the reading or the dwell outside D0, observing before the cancel check, no Unknown on recheck, storing after a cancelled recheck, idle renewal, ordinary reuse, reuse before the power gate) were all killed. The dwell and interval length mutants are killed only by the managed vectors, because the focused tests use the constants.

## Watch out
- Git Bash `perl -pi` on this host rewrote the CRLF docs file to LF, and `grep` and `cat -A` hid the CR bytes. Count with `tr -cd '\r' < file | wc -c` and renormalize with `sed -i 's/\r*$/\r/'`.
