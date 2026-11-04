---
title: Apply every audit dimension across app subsystems
author: Jack
date: 2026-11-03
branch: fix/codebase-reliability-audit
touches:
  - CHANGELOG.md
  - src/OpenBlade.Capture/RzAmdOverclockProbe.cs
  - src/OpenBlade.Contracts/ThxAudioProfiles.cs
  - src/OpenBlade.FirmwareSmoke/FirmwareSmokeRecovery.cs
  - src/OpenBlade.FirmwareSmoke/ReversibleSmokeRunner.cs
  - src/OpenBlade.Service/BatteryConfigurationStore.cs
  - src/OpenBlade.SessionAgent/ServiceAudioMeterLightingLoop.cs
  - src/OpenBlade.Tray.Core/ThxAudioViewModel.Profiles.cs
  - tests/OpenBlade.Contracts.Tests/ThxAudioProfileContractTests.cs
  - tests/OpenBlade.FirmwareSmoke.Tests/ReversibleSmokeRunnerTests.cs
---

## Decided
Use a subsystem-by-dimension coverage matrix for whole-app audits: correctness, performance, optimization, coding practices, responsibility refactoring, and design patterns must each be considered across controls, profiles, input, audio, display, IPC, installation, updates and developer tooling. A telemetry benchmark establishes only its measured workload. Record concrete fixes and justified no-change conclusions separately.

THX profile collections need shared value equality and consistent hashing across JSON round trips. A UI-only comparer misses aggregate persistence verification. Keep actual data comparisons; do not bypass independent verification. Retain the original aggregate's recovery copies if rollback itself fails, and keep subsequent writes quarantined. Audio Meter invalidates its endpoint binding through existing CoreAudio notifications and rebinds under its current capture owner.

## Tried and ruled out
Default-profile notification tests missed nested audio collection identity. Configured audio round trips reproduced both unrelated UI notifications and failed durable save verification. Ordinary rollback-boundary tests did not exercise restoration failure; a destination lock reproduced deletion of all recovery copies.

Do not split every long file or remove repeated integrity checks to fill audit cells. Existing transaction owners, bounded subscriptions, fresh hardware admission and independent verification have concrete responsibilities.

## Watch out
Keep developer validation subject to the same ambiguity and failed-restoration stop rules as production. Saved fan intent is not independent hardware readback. Bound native callback input before allocation and observe cancellation while draining a busy queue. Use fake native seams and disconnected tests for software behavior; preserve separate physical-validation authority.
