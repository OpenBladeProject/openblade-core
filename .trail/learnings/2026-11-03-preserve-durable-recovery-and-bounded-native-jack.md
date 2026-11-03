---
title: preserve durable recovery and bounded native callback ownership
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
  - src/OpenBlade.SessionAgent/WindowsThxActiveOutput.cs
  - src/OpenBlade.Tray.Core/ThxAudioViewModel.Profiles.cs
  - tests/OpenBlade.Contracts.Tests/ThxAudioProfileContractTests.cs
---

## Decided
A failed aggregate rollback needs a durable, exclusively created recovery-required record flushed before any primary replacement. Its presence blocks loads and all saves across new store instances. Throw before user activation changes active context; default profiles are not evidence of recoverable intent. Clear the record before deleting backups, only after verified commit or confirmed rollback, and retain actual path and prior-absence diagnostics on failure.

AudioMeter observes only default Render/Multimedia changes. Notifications invalidate a generation; a routine switch mid-sample rebinds on the next scheduled capture rather than entering native-failure backoff. Keep broader THX notifications unchanged.

Native CPU response buffering belongs to each query, published before the getter for synchronous callbacks. Discard out-of-query events, cap original UTF-8 bytes before allocation, and treat active overflow as failure. Malformed unrelated records are skippable.

## Tried and ruled out
An instance flag and consistent aggregate hashes do not preserve failed-rollback quarantine across repository reload. Broad CoreAudio property callbacks generated false retry errors. A lifetime callback queue accepted stale pre-query records and overflowed on handshake chatter. Removing fresh restoration admission would trade a visible unresolved state for an unauthorized hardware write.

## Watch out
Empty or malformed recovery records still block admission; never automatically interpret artifact paths or restore guessed defaults. Native callbacks lack a correlation ID, so a delayed same-function response arriving during a later active query cannot be distinguished. FirmwareSmoke persisted Custom baselines lack CPU/GPU fields and must refuse restoration rather than guess. Simulated tests establish failure behavior, not physical validation.
