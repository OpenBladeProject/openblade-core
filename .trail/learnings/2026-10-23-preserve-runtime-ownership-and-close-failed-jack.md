---
title: Preserve runtime ownership and close failed telemetry channels
author: Jack
date: 2026-10-23
branch: fix/runtime-cancellation-recovery
touches:
  - Directory.Build.props
  - src/OpenBlade.Protocol/ProductionBatteryPort.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/WindowsVerifiedBladeDeviceProvider.cs
  - src/OpenBlade.Session/DisplayAutomationCoordinator.cs
  - src/OpenBlade.SessionAgent/ApplicationProfileReporter.cs
  - src/OpenBlade.Tray.Core/TelemetryRecordingSession.cs
  - tests/OpenBlade.Protocol.Tests/ProductionBatteryPortTests.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.cs
  - tests/OpenBlade.Service.Tests/WindowsVerifiedBladeDeviceProviderProductionTests.cs
---

## Decided

Fix the six runtime-review issues in shared software layers, without changing device admission, firmware definitions, schemas or IPC. Display transitions own their admitted-write state locally; canceled unowned queries cannot publish either normal results or exceptions, including manual cancellation that does not advance the generation. The profile reporter keeps a bounded latest authoritative snapshot outside the lossy foreground-event queue and applies it at reconciliation.

A failed telemetry writer closes its producer channel before disposal so accepted appends can drain while Stop owns the finalization gate. Preserve the original writer error and partial file. First thermal/fan failures proven to precede submission remain KnownNotApplied and reusable; prior possible submission, uncertain exceptions and cancellation after setter entry retain Indeterminate handling without replay. The initial verified-session cancellation boundary is independent of safe-retry state. After an exhausted HyperBoost budget, shutdown gets one shared five-second final cleanup reserve, preserving keyboard release before fan handback; the worker's cooperative total is at most ten seconds.

## Tried and ruled out

Do not replay uncertain writes, weaken semantic reply validation, infer hardware confirmation from a setter, or rely on a completion observer to unblock a queue while Stop holds that observer's gate. The new telemetry tests use the actual writer and view-model Stop interaction, rather than a recording-session-only shortcut. Do not treat a canceled query's later IOException or returned Failed result as a new hardware ambiguity.

## Watch out

Trail check reports lexical overlap with older status-isolation, observer, display and telemetry notes; these fixes neither accept failed reply bytes nor alter readbacks, sample selection, replay events, firmware capture evidence or physical validation. The only additional no-submit phase is SharedGateAdmission, before native submission, and it does not erase earlier possible submissions. The previous 0.41.2-to-0.41.3 dark-wake version note is historical: freshly fetched main is 0.41.3, so this reliability patch uses 0.41.4. No installed app, service, driver or hardware was changed during development.
