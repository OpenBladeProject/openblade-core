---
title: Native protocol 90 and the pending-parity allowlist
author: Jack
date: 2026-11-24
branch: chore/native-merge-main-a8aea95f
touches:
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/wire.rs
  - tools/openblade-windows-host/src/service_status/unserved.rs
  - tools/openblade-windows-host/src/service_status/observed_capabilities.json
  - tools/openblade-windows-host/src/windows/scm/installed/gpu_temperature_tests.rs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.NativeGpuTemperatureVectors.cs
  - tools/openblade-windows-host/tests/data/ipc85/service.json
  - docs/windows-rust-development-service.md
---

Supersedes the protocol-number part of 2026-11-20 "Native protocol 85 reconciliation with main".

## Decided
- Main a8aea95f used protocols 85 to 89 (85 cooling-pad Auto, 86 HyperBoost Turbo base and entry restriction, 87 Windows Dynamic Lighting, 88 Intel power, GPU overclocking and local dimming, 89 RZ09-0530 MCU edition). The branch's 85 (a confirmed Custom preset with unreported levels) is now 90. Native `PROTOCOL_VERSION`, `wire::CURRENT_VERSION`, the reporter pipe, the status projection, `build/native_windows_fixtures.py` `FRAME_VERSION` and the managed fact all say 90.
- The branch's v85 `service.json` and `service-runtime.json` are frozen under `tools/openblade-windows-host/tests/data/ipc85/` beside the ipc79 and ipc80 copies. The dev host must close every v85 request in them without a reply.
- Native publishes no new main member except two capability nulls: `observed_capabilities.json` carries `hyperBoostEntryRestriction: null` and `blade14KeyboardEdition: null`, as managed writes `DeviceCapabilities.Unavailable()`. `windowsDynamicLightingAdvisory` and `coolingPad` stay absent and decode as defaults. `reportWindowsDynamicLightingAdvisory` and `clearLocalDimming` get the unsupported reply. The operation inventory is 53.
- Pending-parity allowlist: a native replay that fails against a rerecorded managed vector, and cannot be ported mechanically, goes into a `PENDING_PARITY` list in that native test file with the case name and the full managed commit. The main test skips those cases. `every_pending_parity_case_still_fails` fails when a listed case is missing or starts to match. The first list is in `gpu_temperature_tests.rs`: five `reader: live` sampler rows for 8331bbf4 (idle D0 GPU in live readings). The docs list in `docs/windows-rust-development-service.md` "Pending parity with main a8aea95f" names those cases and every main change without a vector.
- The GPU recorder takes an optional step `reader: "live"` that calls `ReadForLiveReadingsAsync`. Without it, the recorder still replays `ReadAsync`, which main did not change, so the old rows stayed identical.
- Product version 0.75.8: a patch over main's 0.75.7, recalculated from the target.

## Tried and ruled out
- Relying on the existing GPU vectors to show the 8331bbf4 gap: they all passed, because the recorder never called the new live-readings path. A gap shows only after the recorder exercises the changed managed method.

## Watch out
- Only three managed vector files changed on rerecord (firmware-version, gpu-temperature, live-reading), and two of them changed only in the v90 comment. Unchanged vectors do not prove native parity for the 40-odd main behavior changes. The docs table lists them as unvectored gaps.
- Native frame recorders (`OPENBLADE_REGENERATE_NATIVE_FIRMWARE_FRAMES`, `..._LIVE_READING_FRAMES`) run only in the `service-live-readings-v1` lane without the Driver-mode read. They write in place.
