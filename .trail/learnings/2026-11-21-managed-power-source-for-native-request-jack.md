---
title: Managed power source for native request checks
author: Jack
date: 2026-11-21
branch: feat/native-thermal-presets
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
---

## Decided
- Managed's power source for request checks is Battery whenever Windows reports battery. `ResolveCurrentPowerSourceAsync` does not sample the adapter in that case. Under Windows AC or unknown power, it samples the adapter, and an adapter battery reply becomes ExternalUnknown, which is blocked.
- Native mirrors this with `thermal_mutation::managed_power_source`. The fan and performance pre-store checks use that source, with its profile and overrides, not the adapter's class.

## Tried and ruled out
- Skipping the checks when the adapter class disagrees with Windows (the old `class_agrees_with_windows`). On the fan path, an adapter reading full power under Windows battery then stored requests that managed rejects, such as ManualCurve under a saved Battery Saver, or anything under a saved Silent or Performance override.

## Watch out
- On the performance path the same case fails fresh admission before the check, so the two paths reach it differently. Do not assume a fence stops the fan path: there, the fan request was stored.
- Under Windows battery, a fan return carrying a saved persistent Balanced or Performance override is now rejected (status 3) before the needs-Windows-AC refusal. That refusal is still reachable mid-request and for presets that pass the Battery check.
