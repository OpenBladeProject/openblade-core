---
title: Freeze Windows host wire and recovery replay
author: Jack
date: 2026-11-09
branch: test/rust-windows-replay-baseline
touches:
  - tests/Build.Tests/PublishPayloadValidation.Tests.ps1
  - tests/OpenBlade.Contracts.Tests/OpenBlade.Contracts.Tests.csproj
  - tests/OpenBlade.Service.Tests/ControlRequestHandlerTests.cs
  - tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj
  - tests/OpenBlade.Service.Tests/ThermalTransitionExecutorTests.cs
  - tests/OpenBlade.Session.Tests/OpenBlade.Session.Tests.csproj
  - docs/windows-backend-replay-inventory.json
  - docs/windows-backend-replay.md
  - tests/OpenBlade.Contracts.Tests/WindowsReplay/v1/service-status-event.json
  - tests/OpenBlade.Contracts.Tests/WindowsReplay/v1/service.json
---

## Decided
Freeze the Windows host boundary in portable JSON with the managed codec and offline dispatch/recovery tests as the oracle. The v79 service inventory is checked against the closed request and frame registries. Session inventories cover the four production codecs. Keep exact-model evidence separate from per-write admission.

## Tried and ruled out
A service wire draft treated unknown outer members as forward-compatible. The production v79 codec rejects them. An omitted ObserveState mask becomes all sensors, and the writer emits both requested and effective masks. A test-only runtime may return a Stored result without confirming any hardware state.

## Watch out
The v51 codec parses compatibility envelopes but the pipe edge rejects mutations and normalizes admitted read requests before the no-activation dispatcher. A portable v51 client whitelist is a projection rather than a full historical server snapshot. Real Windows authentication, handle retirement and uncertain completion still require their owning phases' validation.
