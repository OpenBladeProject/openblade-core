---
title: contain recovery refusal at service boundaries
author: Jack
date: 2026-11-03
branch: fix/codebase-reliability-audit
touches:
  - src/OpenBlade.FirmwareSmoke/FirmwareSmokeRecovery.cs
  - src/OpenBlade.Service/ControlRequestHandler.cs
  - src/OpenBlade.Service/ServiceStartupCoordinator.cs
  - src/OpenBlade.Service/UserConfigurationRepository.cs
  - tests/OpenBlade.Capture.Tests/CpuPowerProbeResponsesTests.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.ConfigurationRecovery.cs
  - tests/OpenBlade.Service.Tests/ServiceStartupCoordinatorTests.cs
  - tests/OpenBlade.Service.Tests/UserConfigurationRepositoryTests.cs
---

## Decided
A durable configuration recovery refusal is a typed operational result at each service boundary. Interactive activation returns a failed service response and retains the previous active context; the next healthy client request remains usable. Machine bootstrap has no previous authoritative intent, so it logs the exact recovery refusal and returns unsuccessful initialization before runtime, run-marker or background work. It must not convert an unresolved transaction into default battery or lighting intent.

An unresolved authorized legacy source may be skipped only when the separate user destination is independently authoritative and nonempty. Preserve the legacy record and source bytes, leave migration incomplete, and refuse empty or corrupt destinations rather than creating new default intent.

## Tried and ruled out
Store-level and direct runtime tests passed while the new exception still escaped the real framed-pipe handler and startup coordinator. Real boundary regressions reproduced these failures. A generic default-valued quarantine at startup would be unsafe because the historical quarantine policy gates ordinary thermal writes, not every nonthermal intent path. Treating a Custom preset name as full baseline confirmation is also invalid when its saved CPU/GPU levels are absent.

## Watch out
Keep cancellation propagation intact and catch the specific quarantine type. Startup refusal is deliberate and diagnosed, not a successful running IPC service. It remains blocked until the original transaction is reconciled; deleting evidence or guessing restoration is not recovery. A routine AudioMeter notification race differs from an actual missing or unactivatable endpoint, which retains normal native failure handling.
