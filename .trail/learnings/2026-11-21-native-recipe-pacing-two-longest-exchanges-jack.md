---
title: "Native recipe pacing: two longest exchanges and the timed append"
author: Jack
date: 2026-11-21
branch: feat/native-thermal-presets
touches:
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/fan.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
---

This supersedes the pacing rule in 2026-11-21-native-thermal-presets-pace-after-prepared-jack.md. That file also lists `touches:` paths under `installed/readiness/` that do not exist. The correct files are below.

## Decided
- Native performance and fan recipes check their pace once, in `Pace::covers` (tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs). The check runs after the Prepared row and after the first setter's own 07/8C read, and before that setter's intent row. The fan path calls it from performance/fan.rs.
- Rule: `now + slowest + (E-1)*second + A*append + fixed <= deadline`.
  - `slowest` and `second` are the two longest exchanges since `Pace::start`, each from send to reply (`Exchanges` in performance.rs).
  - `append` is the Prepared append, timed by `Pace::prepared`.
  - `E` is the exchanges left. `A = APPENDS_PER_SETTER * setters + CLOSING_APPENDS`.
- A refusal marks the attempt complete, and retirement settles the row as known not submitted. Owned Stop cleanup is not paced. The transaction IDs a refused recipe reserved are not reused (documentation only).
- Request checks use managed's power source (`managed_power_source` in tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs), applied in fan_mutation.rs and thermal_mutation.rs. The temporaries come from thermal_storage.rs.

## Tried and ruled out
- Pacing at the mean per exchange: one slow read or append got through and overran.
- Pacing at the slowest interval between completions: one 500 ms stall refused healthy Custom recipes and fan entries, and a uniformly slow device plus journal (350 ms Custom, 650 ms Silent) still started and overran.

## Watch out
- Paced Reader tests cannot stall a synchronous append with a real sleep, because tokio's paused clock does not move. mutation_pace_tests.rs `spend` polls `tokio::time::advance` once instead.
- The derived thresholds at 230 ms exchanges: a Prepared append over 655 ms refuses Silent, and over 255 ms refuses Custom. A single 07/8C stall of up to 1 s refuses nothing.
