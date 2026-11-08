---
title: Keep rolling Stop windows and immediate cutoffs distinct
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - docs/so6120-stop-auto-guardian.md
  - src/OpenBlade.Contracts/FanStop.cs
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.FanStop.Core/FanStopThermalPolicy.cs
  - src/OpenBlade.FanStop.Guardian/Program.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceDutyPlatform.cs
  - src/OpenBlade.Service/BatteryConfigurationStore.cs
  - src/OpenBlade.Service/ProfileStore.cs
  - src/OpenBlade.Service/ServiceControlRuntime.FanStopAutomation.cs
---

## Decided
Custom Stop uses independent, time-weighted trailing CPU/GPU averages. The selected duration is the averaging window, with full coverage required and no extra dwell. Separate configurable immediate cutoffs apply to fresh raw samples. Cutoffs may equal or exceed average limits. Auto and automatic entry retain their prior rules; entry still requires a fresh instantaneous reading under each normal limit.

Schema 25 / IPC 77 adds cutoff fields. Schema-24 migration preserves selected values and sets each cutoff to min(limit + 10, 100), including equality at 100. Update the API and wire-shape golden fixtures as well as versioned roundtrip tests.

Continuity and invalid-sample failures outrank thermal completion in both continuation and command admission, because thermal completion may rearm automatic entry. Command reads never credit or reset average history. GPU D3 clears only the GPU window; wake starts fresh GPU coverage.

## Tried and ruled out
Continuous-over-limit timers reset on each cool sample and do not represent the selected rolling-average behavior. Sample-count averaging overweights rapid samples. Reusing command reads for elapsed coverage changes the policy according to mailbox activity.

A Custom CPU65 installed trial on the previous continuous-timer build passed initial admission but failed the first refreshed entry check before any EC command. It conclusively released ownership and restored original Custom55/55/5 settings. Later heat did not identify the actual failed predicate. This was not successful Stop/Auto restoration validation.

## Watch out
Entry refusal diagnostics must reset per attempt and label thermal sampling unattempted when an earlier prerequisite prevented it. Preserve existing query ordering and write admission; diagnostics need no extra hardware read. An ordinary Auto state after a zero-command refusal is not evidence of restored fan control. Source tests and disconnected UI renders do not install or physically validate the new policy.
