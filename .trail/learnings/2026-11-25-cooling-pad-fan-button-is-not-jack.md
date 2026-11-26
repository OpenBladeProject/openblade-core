---
title: Cooling pad fan button is not detectable
author: Jack
date: 2026-11-25
branch: fix/cooling-pad-false-rejection
cost: { minutes: 184, tokens: 1230913 }
touches:
  - CHANGELOG.md
  - docs/cooling-pad.md
  - Directory.Build.props
  - src/OpenBlade.Protocol/CoolingPadProtocol.cs
  - src/OpenBlade.Protocol/SourceBackedCoolingPadDevice.cs
  - src/OpenBlade.Service/CoolingPadControlRuntime.cs
  - tests/OpenBlade.Protocol.Tests/CoolingPadSourceProtocolTests.cs
  - tests/OpenBlade.Service.Tests/CoolingPadControlRuntimeTests.cs
  - src/OpenBlade.Tray.Core/MainViewModel.cs
  - src/OpenBlade.Tray.Core/Properties/Resources.qps-ploc.resx
---

## Decided
The RC21-0230 pad fan button is not detected, and OpenBlade adds no way to detect it. In Automatic or a host curve the next 0D/01 demand replaces the button speed; Fixed sends no demand, so the button speed stays while status shows the confirmed record. The user decided on 2026-11-25 that Automatic taking over is acceptable and that users choose Fixed to use the fan button; docs/cooling-pad.md and the changelog say so.

## Tried and ruled out
Comparing the fan record: a press left the record OpenBlade compares unchanged (firmware owner in Automatic, Synapse owner in Fixed) while the fan changed. Listening for input report 5 (Synapse declares CycleFanSpeedMode, TurnOnFan, TurnOffFan on it): a read-only listener on the six readable 1532:0F43 input collections got nothing in Normal mode; the mi_01 col01 consumer-control collection is held by Windows (access denied). Synapse writes a button mapping mode (209 for the fan button) and runs in Driver mode; both are unadmitted writes for this purpose.

## Watch out
A fan-button press looked like a successful restore under Restore them: the fan changed and came back. That was the demand overwrite, not detection, and no log showed a fan outside change. Check the status message, not the fan speed, before claiming outside-change detection works. The fan button also changes the pad lighting, which is detected. The pinned Synapse bundles download from apps.razer.com and match the hashes in the guide.
