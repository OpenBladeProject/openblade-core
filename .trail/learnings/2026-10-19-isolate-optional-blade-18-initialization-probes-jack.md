---
title: Isolate optional Blade 18 initialization probes
author: Jack
date: 2026-10-19
branch: fix/739-blade18-initialization
touches:
  - Directory.Build.props
  - docs/blade18-observer-evidence.md
  - src/OpenBlade.Protocol/ExperimentalBladeFactory.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.StartupAnimation.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.TemporaryFullCharge.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - src/OpenBlade.Protocol/FirmwareQueryDiagnostics.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeFactoryTests.cs
  - tests/OpenBlade.Protocol.Tests/FirmwareQueryDiagnosticsTests.cs
  - tests/OpenBlade.Protocol.Tests/WindowsRazerHidSessionTests.cs
---

## Decided
Optional feature reads must not gate the experimental factory's shared control session. Startup animation already has an independent service probe through potential capabilities; temporary full charge is explicitly read with battery state. Keep both read-gated mutation capabilities and their fresh write baselines. Optional Blade 18 keyboard-layout metadata may poison a session: fully dispose and confirm drainage before one fresh exact host/controller/interface admission, without repeating that probe or guessing a layout. Successful deferred reads promote capabilities on the next refresh.

## Tried and ruled out
The 2026-10-19 owner diagnostics (nightly.94) show exact host/HID admission and valid selectors but intermittent startup getter status failures. The old factory swallowed only healthy-session IO errors, so real status rejection escaped before a battery query and became a generic battery-state failure. Real-session offline tests reproduce it. Candidate handle disposal does not close the selected session because BindSelected transfers ownership. Reusing a poisoned session, disabling protocol checks, or fabricating a false temporary-full-charge value are not fixes. The owner bundle does not retain the rejected status byte. No new Synapse capture is needed to fix this software coupling.

## Watch out
Keep startup and full-charge writes behind valid explicit readback; this supersedes eager factory initialization, not their source-backed admission. A genuine full-charge query failure still invalidates aggregate battery truth. NativeDeviceReadException must preserve the actual query and failure diagnostic for experimental reads too. The firmware diagnostics allowlist must match actual variable-response GET specs (0F/98 and 07/8F, [0], bounded validator), otherwise failed optional probes disappear from exports. Bounded metadata recovery must stop on cancellation, cleanup failure or failed fresh admission. No new firmware command, physical-validation claim, DLL telemetry, driver operation or installation is authorized.
