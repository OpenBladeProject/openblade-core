---
title: Cooling pad resumes saved settings after service handback
author: Jack
date: 2026-11-22
branch: fix/cooling-pad-restart-resume
cost: { minutes: 96, tokens: 661277 }
touches:
  - tests/OpenBlade.Protocol.Tests/CoolingPadSourceProtocolTests.cs
  - CHANGELOG.md
  - Directory.Build.props
  - docs/cooling-pad.md
  - src/OpenBlade.Protocol/SourceBackedCoolingPadDevice.cs
  - src/OpenBlade.Service/CoolingPadControlRuntime.cs
  - src/OpenBlade.Service/CoolingPadStateStore.cs
  - src/OpenBlade.Service/ServiceControlRuntime.CoolingPad.cs
  - tests/OpenBlade.Service.Tests/CoolingPadControlRuntimeTests.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntime.CoolingPadLifecycleTests.cs
---

## Decided
The service hands the RC21-0230 pad back to its original baseline at sleep, at shutdown and whenever the service stops (each development install included). Before PR #1004, nothing applied the saved settings again. The handback stays. An Apply that takes control now writes a per-user `resume-<sha256(sid)>.json` next to the settings file, and Release control deletes it. `CoolingPadControlRuntime.ResumeSavedControlAsync`, driven by `MaintainCoolingPadAsync`, reruns the ordinary `ApplyAsync` after a clean handback. It uses the Apply admission plus `fanStopShutdownStarted == 0`. Byte 0 of `0F/02` is openrazer's VARSTORE/NOSTORE store flag, so the strip shows the last effect written. `ReadCore` reports a lighting mode only when profiles 1 and 0 hold the same effect.

## Tried and ruled out
Adding a resume flag to `CoolingPadSettingsDocument`: the store disallows unmapped members, so a downgraded service would fail to load its settings. Auto-reading after a retained journal or paused writes and then resuming: that would replay an uncertain write. Writing built-in effects to profile 0 as well: it changes a validated command sequence without device evidence. The theory that a lit profile 0 beside a saved profile 1 Off lit the pad is unconfirmed: a read-only read after the install found both records Off.

## Watch out
Existing users have no resume record, so nothing resumes until their first Apply on the new build. The tray never shows `ConfirmedLightingMode`, only brightness, so the lighting fix matters for status and diagnostics only. A sleep/resume check needs a present user. To read the pad without writing, use `ServiceClient.ReadCoolingPadStatusAsync` (namespace `OpenBlade.Tray`), not raw GETs.
