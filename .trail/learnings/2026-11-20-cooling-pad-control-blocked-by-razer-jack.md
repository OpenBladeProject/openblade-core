---
title: Cooling-pad control blocked by Razer driver stack
author: Jack
date: 2026-11-20
branch: fix/cooling-pad-razer-driver-stack
cost: { minutes: 29, tokens: 288930 }
touches:
  - src/OpenBlade.Protocol/SourceBackedCoolingPadDevice.cs
  - tests/OpenBlade.Protocol.Tests/CoolingPadSourceProtocolTests.cs
  - CHANGELOG.md
  - Directory.Build.props
  - docs/cooling-pad.md
  - src/OpenBlade.Protocol/HyperBoostEligibilitySource.cs
  - src/OpenBlade.Protocol/WindowsCoolingPadDeviceProvider.cs
  - src/OpenBlade.Protocol/WindowsCoolingPadHidSessionOpener.cs
  - tests/OpenBlade.Protocol.Tests/WindowsCoolingPadDiscoverySourceTests.cs
  - tests/OpenBlade.Protocol.Tests/WindowsCoolingPadHidSessionOpenerTests.cs
---

## Decided
Pad fan/lighting never applied on hosts with Razer's driver stack (RZCONTROL/RZVIRTUAL). Two blockers: (1) the opener required every 1532:0F43 MI_00 path to be the exact control, and StillExact (per-SET identity) required exactly one path, but the stack re-exposes input-only MI_00&Col03&Col01/Col02 children; (2) the pad guard blocks any running service named Razer, and RazerExperienceService (ONNX/DirectML runner, no hid.dll/SetupAPI/DeviceIoControl imports) always runs. Fix (PR #983): shared WindowsCoolingPadDiscoverySource.IsInputOnlySibling = same VID/PID with zero feature report length, derived from the firmware updater descriptor (interface 0 has exactly one unnumbered 90-byte feature report; report ID 5 is input only). The opener classifies with metadata-only handles, never opens siblings for I/O, then opens and re-reads the single exact control. RazerExperienceService is exempted by exact service name only. Pad errors now name the failing command and the session's validation reason.

## Tried and ruled out
The user asked for source-backed (firmware) evidence only, so the earlier captured rule (revision 0101 plus two usages) was replaced, not reused. Do not send raw pad GETs from a scratch tool to debug; widen the typed error message instead. After both fixes the explicit read reached the device, then failed with ProtocolFailure. That third issue was still open when this note was written.

## Watch out
Local build host: rustup proxy missing (only toolchain 1.99.1 dir), vswhere not on PATH (VsDevCmd needs it), crates must be fetched once (cargo fetch --locked). A batch shim that drops "+1.99.1" must forward raw %* (cmd splits on '='), or the native provenance check fails. PS 5.1: run deployment via Start-Process with -Redirect*, not *> (cargo stderr becomes terminating). The broker still rejects the v2 TPM key; install the C:\tmp build bundle attended (normal integrity, user approves UAC).
