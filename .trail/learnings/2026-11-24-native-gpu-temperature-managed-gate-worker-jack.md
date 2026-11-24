---
title: "Native GPU temperature: managed gate, worker and NVML load"
author: Jack
date: 2026-11-24
branch: feat/native-gpu-temperature
touches:
  - ".github/workflows/rust.yml"
  - docs/windows-rust-development-service.md
  - rust/crates/openblade-platform-windows/src/gpu_apps.rs
  - rust/crates/openblade-platform-windows/src/gpu_apps/sampling.rs
  - tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.NativeLiveReadingVectors.cs
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/windows/scm/installed/live_readings.rs
  - tools/openblade-windows-host/src/windows/scm/installed/live_readings_tests.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
---

## Decided
- Native GPU temperature (`service-live-readings-v1`, mask bit 8) mirrors managed's live-readings path, `SafeWindowsFanTemperatureSource.ReadAsync` with idle sampling off: workload first (WDDM GPU-engine counters, fresh query, primed, read again after 150 ms, PID 4 excluded, > 0.1 %), then the SetupAPI `CM_POWER_DATA` D-state of the one `PCI\VEN_10DE&` display device. NVML is touched only for a workload on D0, and a power recheck after the read discards a non-D0 value. The session lease is 5 s after the last valid active sample; every other sample releases it, except a cancellation before the power read.
- The counter query lives in the platform crate (`gpu_apps::NvidiaActivityQuery`, reusing the GPU-app sampler's PDH/DXGI code), mirroring managed `WindowsNvidiaGpuActivityReader`. The policy (window, PID 4, threshold, power, lease) lives in the host's `gpu_temperature.rs`, mirroring managed `GpuActivitySource` and the fan temperature source.
- One worker thread per read source, started by the first request, with a `sync_channel(1)`: one request may wait, any further request gets None. The observation requests beside the HID job and awaits only until 500 ms before its deadline; Stop drops the sender and never joins.
- nvml.dll is loaded with `LOAD_LIBRARY_SEARCH_SYSTEM32` and four `GetProcAddress` symbols, never unloaded. No Authenticode check, matching managed's `[DllImport("nvml.dll")]`; that trust choice is the orchestrator's to revisit.
- A value above the v85 codec's 115 C bound is published as null (managed's codec would refuse the whole reply), as the fan RPM bound already does.
- `gpu-temperature-vectors.json` is recorded by `ServiceControlRuntimeTests.NativeGpuTemperatureVectors` over managed's real source with scripted activity, power, session and timer.

## Tried and ruled out
- Releasing in the worker loop before skipping an abandoned request, and setting the renewal inside the gate: both were redundant (equivalent mutants), so `sample()` now renews or releases in one match and the first cancellation check covers abandoned requests.
- A 100 ms request window in the Stop test: under ~90 % host load the worker thread did not take the request in time. The test now warms the worker up with a failed init first and uses a 1 s window.

## Watch out
- Native samples before the HID job's outcome is known, so a job that then fails may still have read an active D0 GPU and renewed the lease; managed reads the GPU only after a successful device read. It never starts NVML for an idle, D3 or unknown GPU.
- The service image now imports pdh.dll and dxgi.dll, which neither package import allowlist (`native_package.rs` SYSTEM_IMPORTS, managed `WindowsNativePackage.IsSystemImport`) admits. The live-readings image is refused by its marker anyway; admitting it needs those imports reviewed.
- `Pending::temperature` consumes the receiver, so a timed-out observation closes the reply and the worker sees it as abandoned; the `until` check covers a request whose observation has not reached its await yet.
