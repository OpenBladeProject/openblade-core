---
title: Show controller-blocked startup as unavailable controls
author: Jack
date: 2026-11-12
branch: fix/950-startup-controller-conflict
touches:
  - Directory.Build.props
  - src/OpenBlade.Tray.Core/MainViewModel.cs
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - tests/OpenBlade.Tray.Tests/MainViewModelControllerAvailabilityTests.cs
---

## Decided
Issue #950 diagnostics identify the exact RZ09-0529 and pass host admission, then stop at ConflictCheck/ConflictBlocked before HID enumeration. Potential controllers include RazerAppEngine and razerwdl. The bundle does not establish a missing HID interface or unsupported model. Show Controls unavailable, a readiness warning, and the existing conditional Synapse/read-only check guidance when unverified blocked state accompanies a controller advisory. Preserve unsupported, firmware-blocked, and checking precedence. This shared presentation correction benefits every model; hardware admission is unchanged.

## Tried and ruled out
No new model admission or firmware commands are needed. The existing conflict test only exercised an already verified laptop, missing the initial unverified/controller combination. A missing synapse-blade-blocker.json means no verified blocker exemption and does not justify bypassing controller safety or stopping software automatically.

## Watch out
Controller presence alone cannot prove the failed probe reason or laptop presence. Clearing the controller must not claim Ready until verification succeeds. Keep issue #950 open for owner confirmation; software tests and disconnected screenshots do not prove physical recovery. Developer snapshot state controller-blocked exercises the initial presentation without live service or hardware access.
