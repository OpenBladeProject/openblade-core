---
title: Integrate opt-in Battery replay with private Windows GPU and Display
author: Jack
date: 2026-11-10
touches:
  - docs/windows-rust-development-service.md
  - tools/openblade-windows-host/src/session_windows.rs
  - tools/openblade-windows-host/src/session_windows/integrated.rs
  - tools/openblade-windows-host/src/session_gpu.rs
---

## Decided
Keep the exact GPU/keyboard and Battery acquisition parents. Keep the old integrated startup arguments and admit the fixed Battery replay only with battery-available, under the existing private pipe name, claim and per-frame peer identity. The only managed runtime change is stable order for equal-start intervals.

## Tried and ruled out
The exact first-parent Trail check flagged lexical matches in the development documentation. The runtime diff does not add native report acquisition, change the typed contracts, enable GPU close in the default build, or permit any setter to substitute desired values for readback. Retain the documentation of those limits.

## Watch out
The GPU disconnect read still requires AsyncReadExt after moving first-frame decoding into a shared helper. The host SDK features changed even for the ordinary service build, so rebuild both ordinary roles. Build the simulated GPU close fixture separately and never use it for the signed scratch roles.
