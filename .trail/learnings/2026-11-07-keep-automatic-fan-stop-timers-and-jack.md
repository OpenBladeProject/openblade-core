---
title: Keep automatic fan Stop timers and guardian ownership separate
author: Jack
date: 2026-11-07
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - docs/so6120-stop-auto-guardian.md
  - src/OpenBlade.Contracts/FanStop.cs
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Contracts/ServiceMessages.cs
  - src/OpenBlade.FanStop.Guardian/Program.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceAdmissionPolicies.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceDutyPlatform.cs
  - src/OpenBlade.Service/BatteryAutomationPreferences.cs
  - src/OpenBlade.Service/BatteryConfigurationStore.cs
---

## Decided
Keep End Stop and Automatic Stop as independent foldable subcards. Auto retains the existing conservative policy. Custom uses separate CPU/GPU continuous over-limit timers; GPU workload alone cannot end it. Automatic Stop is opt-in, defaults to both temperatures strictly below 50 C for 30 seconds, and starts a fresh interval after a verified thermal handback. Manual Auto pauses entry until Resume. Mode, upward-power, lifecycle, and fault conditions pause it; existing owned lower-power retention remains.

Persist user policy separately from transient ownership. Pin the policy into each guardian reservation. Finish verified Auto before changing an owned thermal policy. Re-entry requires the same operation to complete its journal, close native handles, and exit. Entry or cleanup ambiguity never creates a retry loop. Close waiting automatic-entry admission at lifecycle ingress even when no Stop owner currently exists.

## Tried and ruled out
GPU workload absence is not GPU power-down evidence. Use an actual GPU temperature for D0, or explicit Windows-reported D3 evidence; unknown/malformed/multiple-device power data does not mean cool. CM_POWER_DATA reports the latest OS D-state and does not prove physical D3cold or no-wake behavior. Do not carry elapsed time across missing or backward samples, combine hot time from different sensors, or overwrite a partial UI draft on unchanged status polling.

## Watch out
Initial entry still uses only the existing exact SO6120 EC 1.04 USB-C Balanced backend. This software work does not establish new model, Silent, installed Session-0, sustained-Stop, or physical sensor validation. Input bounds and warnings are not validated thermal ceilings. Configuration migration must default automation off; IPC changes require coordinated component deployment. Validate WinUI template-root bindings against the actual template data type and use Control wrappers for enabled-state inheritance, not StackPanel.IsEnabled.
