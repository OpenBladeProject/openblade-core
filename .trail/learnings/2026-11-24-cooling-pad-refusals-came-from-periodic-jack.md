---
title: Cooling pad refusals came from periodic power classification
author: Jack
date: 2026-11-24
branch: fix/cooling-pad-false-rejection
cost: { minutes: 92, tokens: 324663 }
touches:
  - docs/cooling-pad.md
  - src/OpenBlade.Service/ServiceControlRuntime.CoolingPad.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntime.CoolingPadAuthorityTests.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntime.CoolingPadLifecycleTests.cs
  - CHANGELOG.md
  - src/OpenBlade.Service/CoolingPadControlRuntime.cs
  - src/OpenBlade.Tray.Core/CoolingPadViewModel.cs
  - tests/OpenBlade.Service.Tests/CoolingPadControlRuntimeTests.cs
  - tests/OpenBlade.Tray.Tests/CoolingPadViewModelTests.cs
---

## Decided
The intermittent cooling-pad "did not accept" came from the 15 s periodic power classification (`RefreshPowerSourceClassificationAsync`), which advanced `powerAdmissionEpoch` on every tick even when nothing changed. Laptop writes hold `operationGate`, which that tick skips, but the pad has its own gate, so any overlapping pad command was refused: Blocked "authority changed before submission", Failed "ConflictBlocked (0F/02, Guard)", or Failed "submission is uncertain" after part of the change reached the pad. The pad now uses `coolingPadPowerEpoch`, advanced only in `HandlePowerSourceChangedAsync` (Windows AC/DC notifications). On the RZ09-0581 with the RC21-0230 it went from 50-57 of 60 applied to 60/60 and 40/40.

## Tried and ruled out
Battery-percentage power broadcasts (the service only registers GUID_ACDC_POWER_SOURCE) and Razer services or processes starting (none changed in a 150 ms poll). "ConflictBlocked ... Guard" does not mean a competing controller: `SubmissionAllowed()` returning false is reported with the same kind and phase. Plain `Trace.TraceWarning` from the service does reach the exported `logs.jsonl`, which is the quickest way to instrument admission on hardware.

## Watch out
Another session can install a different broker build mid-test (the function-row session did at 18:12). Check the installed `OpenBlade.Service.dll` ProductVersion before trusting a hardware run. A deployment stop leaves an uncleared run marker, so a retained pad ownership journal makes RecoveryRequired true and blocks resume until the next Apply. Auto-mode pads report changing target RPM because OpenBlade writes host demands; that is not an outside change. CPU-temperature read timeouts still cause an occasional Blocked "curve is waiting for a reading"; it saves nothing and the tray retries.
