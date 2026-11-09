---
title: Compare per-battery direction flags with reported hardware-monitor rates
author: Codex
date: 2026-10-31
branch: fix/battery-discharge-detection
touches:
  - src/OpenBlade.Service/WindowsPowerStateProvider.cs
  - src/OpenBlade.Service/WindowsBatteryHealthReader.cs
---

## Decided
The standard Windows per-battery IOCTL_BATTERY_QUERY_STATUS API returns explicit BATTERY_DISCHARGING and BATTERY_CHARGING PowerState flags as well as signed rate, capacity and voltage. These flags are a candidate corroborating direction signal when investigating aggregate SYSTEM_BATTERY_STATE rate disagreements. A diagnostic comparison was implemented and exercised locally; this does not change production detection or establish behavior during the failing workload.

## Tried and ruled out
LibreHardwareMonitor's reviewed Battery.cs uses the per-battery status IOCTL, divides signed Rate by 1000 for watts, and chooses the displayed direction from its sign. HWiNFO's author describes battery monitoring circuit/EC reported rate, but does not establish one universal Windows API for all models. CPUID's reviewed HWMonitor page documents the sensor without the acquisition path. Do not attribute an undocumented proprietary implementation to those applications.

## Watch out
Use immediate read-only requests, fresh valid battery tags, deterministic handle cleanup and explicit query failures. Filter actual system batteries and distinguish relative units. Keep direction separate from an unavailable wattage magnitude; contradictory flags/rate are diagnostic disagreement, not permission to fabricate a corrected sign. Different Windows access paths can still share the same battery driver and firmware.

Sources: https://learn.microsoft.com/en-us/windows/win32/power/battery-status-str ; https://learn.microsoft.com/en-us/windows/win32/power/ioctl-battery-query-status ; https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/Battery/Battery.cs ; https://www.hwinfo.com/forum/threads/charge-rate-different-to-measured-value.3203/ ; https://www.cpuid.com/softwares/hwmonitor.html
