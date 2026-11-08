---
title: Restore temporary performance intent after fan test service restart
author: Jack
date: 2026-11-04
branch: feat/battery-saver-low-fan-validation
touches:
  - build/Invoke-BatterySaverLowFanValidation.ps1
  - build/Invoke-SilentLowFanValidation.ps1
  - docs/silent-low-fan-validation.md
  - src/OpenBlade.Capture/Program.cs
  - src/OpenBlade.Capture/SilentLowFanValidationCommand.cs
  - src/OpenBlade.Protocol/SilentLowFanValidationRunner.cs
  - tests/OpenBlade.Protocol.Tests/SilentLowFanValidationRunnerTests.cs
  - build/LowFanValidationInstalledState.ps1
  - docs/evidence/silent-2000-to-1500-rz09-0581-2026-11-05.json
  - tests/Build.Tests/LowFanValidationInstalledState.Tests.ps1
---

## Decided
A confirmed hardware round trip is not complete installed-state restoration when the service restart drops a temporary performance override. UntilPowerSourceChange overrides are intentionally volatile; the Silent trial restored exact3800 targets and05/00 firmware mode, but startup selected the savedBalanced profile after losing the temporarySilent override. Fresh typed readback proved that mismatch. A separately journaled one-shot SetPerformanceConfigurationOverrideAsync restored the exact original object/lifetime; all10 saved intent fields then matched. Shared wrapper handling belongs in LowFanValidationInstalledState.ps1 and applies to bothSilent andBatterySaver developer wrappers, without changing historical trial or100-RPM incident artifacts.

## Tried and ruled out
Do not deserialize the whole saved ServiceStatusSnapshot with defaultJSON options: its IReadOnlySet capabilities require IPC converters. Keep the original typed object in the wrapper, including optional custom/GPU fields. Do not treat SafeToResume orzero writes alone as proof that installed intent matches. Permit only the observed missingUntilPowerSourceChange override case after confirmed unambiguousprobe restoration, fresh knownAutomatic/verifiedActive admission, samepower/profile/context/otherintent and onlyexpectedderivedPerformance.Value/Source drift. Journal beforeoneIPC; readback all10 fields; no retry or unrelated fallback.

## Watch out
Exact0581 Silent/manual retained1500 targets but actualfans stayed1900–2000 through30 samples; this doesnot validateSilent fanstop, unlike the separateBatterySaver physicalresult. Keep the negative result and later softwareoverride restoration separate. A literalzero target is a separate prepared developer experiment: exactsource encodesbyte00 withoutspecialcase, but firmwaremeaning remainsunknown. It requires itsown explicitliteral0 authorization froma presentuser, exactbuildreview and the samefreshguards/readback/exactrestore; never inferpermission fromthe prior100 choice or callzero a knownFanOff command. Nonzero calibration is disjoint andcannotselectzero.
