---
title: Exact-head review probes for cancellation classification and queue failure recovery
author: Jack
date: 2026-10-23
touches:
  - tests/OpenBlade.Protocol.Tests/ProductionBatteryPortTests.cs
  - tests/OpenBlade.Service.Tests/WindowsVerifiedBladeDeviceProviderProductionTests.cs
  - tests/OpenBlade.Session.Tests/DisplayAutomationCoordinatorTests.cs
  - tests/OpenBlade.SessionAgent.Tests/ApplicationProfileReporterTests.cs
---

## Decided

At 6bc1872ed08105c539f2676442a89f4ea48b9e4d, isolated fake-device regressions reproduce two false Indeterminate classifications: cancellation after the initial verified port opens but before the setter, and SharedGateAdmission refusal of the first multi-command thermal setter. No production fix was applied. The existing fresh-retry cancellation test passes, so first-attempt and retry admission need separate coverage.

A failure-injection probe against the unchanged Tray.Core also reproduces a telemetry stop cycle: a full bounded channel has a blocked append, StopTelemetryRecordingAsync holds telemetryRecordingGate while draining it, the writer faults without closing its channel, and the completion observer waits for that same gate before it can dispose the session. Completing the channel with the writer error is a distinct recovery obligation from normal Stop.

## Tried and ruled out

A recording-session-only probe is insufficient to prove a product hang because a direct StopAsync closes the channel. Reproduce the view-model drain and completion-observer interaction as well. Source inspection and fake-device tests do not establish physical hardware behavior.

## Watch out

Temporary regression tests exist only in the isolated review worktree and deliberately fail against the reviewed baseline. They are review evidence, not fixes. Keep exact submission-boundary evidence when changing classification, and preserve Indeterminate after a possible prior command submission. An authoritative profile-status event also shares a DropOldest queue with foreground events; validate overflow before relying on boundedness alone.
