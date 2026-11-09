---
title: Review Silent manual commands before testing low fan targets
author: Jack
date: 2026-11-04
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Capture/Program.cs
  - src/OpenBlade.Protocol/BatteryValidationFactory.cs
  - src/OpenBlade.Protocol/ProductionBatteryPort.cs
  - src/OpenBlade.Protocol/ThermalModeProtocol.cs
  - tests/OpenBlade.Protocol.Tests/ThermalModeProtocolTests.cs
  - build/Invoke-SilentLowFanValidation.ps1
  - docs/silent-low-fan-validation.md
  - src/OpenBlade.Capture/SilentLowFanValidationCommand.cs
  - src/OpenBlade.Protocol/SilentLowFanValidationRunner.cs
  - tests/OpenBlade.Capture.Tests/SilentLowFanValidationCommandTests.cs
---

## Decided
Exact RZ09-0581 Product736 vendor source separately establishes Silent manual fans as 0D/02 [1,zone,5,1]. The pinned main module3327 maps Silent to5; policy50779 supplies profile1/manual1 and speed/100, and the base device emits the known 4-byte mode and 3-byte target geometry. The full-power policy preserves Silent; limited external power substitutes Balanced. This supports a closed full-power Silent developer experiment, not a universal model capability. Add read-only05/01 decoding without granting other-model writes. Physical results and production admission remain separate gates.

## Tried and ruled out
The old decoder's absent SilentManual state was an implementation gap, not evidence that firmware cannot preserve Silent. Do not invent05/01 from SilentAutomatic05/00 alone, or substitute Balanced while claiming Silent. Retrieve the exact pinned vendor bytes as data, check complete hashes, and inspect them without executing vendor code. Keep the failed100 RPM Battery Saver incident and its restoration path unchanged; neither0nor100 is part of the new experiment.

## Watch out
Confirm Silent/manual by independent pairedGET before any targetSET, then prove spinning at2000 before a1500 target-only reduction. Calibration uses the closed1900..1500 band in100 increments and a20-second minimum dwell before accepting stable spinning; early positive tach can be spin-down momentum. Stop candidates when eitherfan stops, clamps or remainsunstable. Ambiguouswrites/reads/native retirement stop allwrites and require read-onlyreconciliation. Everywrite keeps fresh exactidentity/controller/localconsole/AC/full-adapter admission; trials additionally require CPU<=55 and confirmedinactiveGPU with timestampsbeforeasyncsensors. Warmrestoration skips onlytrialtemperature checks. Confirm exactsavedtargets+SilentAuto before resuming installedowners; noinstalledfiles are replaced. Historical baseline alreadyzero cannot establishcausation, and a source-backed experiment doesnot claimphysicalfanstop until tested.
