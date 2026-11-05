---
title: Keep passive battery observations separate from thermal control
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - docs/so6120-direct-duty-safety.md
  - docs/so6120-silent-automatic-policy.md
  - src/OpenBlade.Capture/Program.cs
  - tools/firmware/README.md
  - docs/so6120-ec-pwm-writers.md
  - docs/windows-battery-observation.md
  - src/OpenBlade.Capture/WindowsBatteryObservationCommand.cs
  - src/OpenBlade.Capture/WindowsBatteryObservationReader.cs
  - tests/OpenBlade.Capture.Tests/WindowsBatteryObservationCommandTests.cs
---

## Decided

A passive thermal experiment must trace the entire status request, not just GetStatusAsync. Current interactive GetStatus/Subscribe can fall through to ActivateUserAsync on a cache miss or deferred recovery. Protocol-51 cached compatibility acquires a thermal-admission lease. Neither is an inert observer. Use an isolated Windows battery-class reader for optional battery temperature without service requests, HID, raw EC access, or profile changes.

Keep query timestamps distinct from sensor acquisition time. LiveReadings has no per-reading timestamps; revisions and repeated values do not prove fresh telemetry. A native query deadline belongs around a child process with bounded output and confirmed retirement, not a background thread around synchronous DeviceIoControl. Unsupported temperature stops the recording instead of yielding zero or repeating an uninformative measurement.

## Tried and ruled out

A read-only-looking service API was insufficient because user activation can mutate hardware. The older cached-status shortcut learning was useful for locating the code but did not establish experimental passivity. Windows battery queries are optional: native error 1 is unsupported; a sandbox OpenBattery access denial is a separate observation. Never reinterpret either as a physical temperature.

The SO6120 PWM writer census found lifecycle selector resets and internal EC watchdog service, but no bounded host-loss handback in recovered paths. A command-driven raw-address writer bypassing a bank alias does not provide transport ownership, durable emergency cooling, or authority to test it.

## Watch out

InvalidDataException derives from SystemException, not IOException; catch it explicitly when retiring a child after malformed/oversized output. Bound the aggregate await independently of pipe cancellation. Avoid a final fractional delay followed by another sample when documenting a one-query probe. Firmware recovery conclusions remain bounded by recovered listings and cannot be inherited by other models.
