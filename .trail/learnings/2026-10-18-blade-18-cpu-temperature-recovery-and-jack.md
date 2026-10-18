---
title: Blade 18 CPU temperature recovery and standalone telemetry boundary
author: Jack
date: 2026-10-18
branch: feat/blade18-observer-support
touches:
  - CHANGELOG.md
  - docs/blade18-observer-evidence.md
  - docs/blade18-source-feature-recovery.md
  - docs/experimental-blade-support.md
  - src/OpenBlade.Protocol/ExperimentalBladePort.Custom.cs
  - docs/blade18-temperature-source-recovery.json
  - tests/OpenBlade.Protocol.Tests/ExperimentalPowerModeObservationTests.cs
---

## Decided
Blade 18 temperature recovery found no standalone CPU source in the reviewed Product 672 path. Its exact middleware does not enable systemHelper or hasSystemMonitoringStatistics. Shared Series11 monitoring selects measured CPU Package values through RzSystemCommon, RzSystemService and CPUID SDK; that is architectural evidence, not an admitted Blade 18 provider. Keep telemetry independent of Synapse DLLs/services/drivers. Existing Windows PDH TZRZ paths are tried on Blade 18 already; NVIDIA NVML remains conditional on active GPU status.

## Tried and ruled out
BladeDLL exports display/tuning/identity operations, not a recovered CPU temperature getter. Intel tuning requires XTU service. CPUID PDH counters found are utilization and disk I/O, not temperature. Neither arbitrary thermal-zone enumeration nor a chart/thermal limit establishes CPU package temperature. Missing exact-model enablement does not prove hardware cannot implement full charge, startup animation or asymmetric fan targets. Function-primary remains an OpenBlade per-device input-suppression gap.

## Watch out
The existing Observer bundle reported zero MSAcpi thermal-zone rows; that does not prove PDH is empty. Do not claim variable Blade 18 curves now work: missing CPU input holds 3800 RPM. Decoding 0x04 in an already admitted Power Mode Control query is only a diagnostic bit observation, not charging behavior confirmation or new setter admission.

Trail check repeated keyword matches across the whole branch, including unrelated visual-test notes. The reviewed delta only adds decoded query-bit labels and source findings: it enables no primary inversion, native activation, unknown command or vendor telemetry dependency. The earlier reconciliation note remains applicable.
