---
title: "Native live readings: fan RPM, battery flow and health, HyperBoost capabilities"
author: Jack
date: 2026-11-23
branch: feat/native-live-readings
---

## Decided
- Native live readings live behind `service-live-readings-v1` (implies mailbox observation; packaging refuses feature and marker, like the Driver-mode read). Fan RPM is the capture-backed exact RZ09-0581 0D/88 pair on the same read job (`Queries { fan_rpm }`), after the thermal getters, with its own 1 s budget and 500 ms reserve.
- The ObserveState mask reaches the read source through a tokio task-local set by `StatusRequest::scope_live_readings` around the synchronous `observe_performance` call. ReadSource reads it while creating the observation, not inside the future. This kept the readiness edit to two lines and the StatusSource trait unchanged.
- Fan speeds are retained for 2 consecutive failed jobs within RETENTION_WINDOW under the same ReadAdmission (managed nulls at once). A speed above 20,000 RPM counts as a miss: the managed codec refuses it, and a 0D/88 byte can encode 25,500.
- Battery health follows managed's schedule: first status, then one retry after the first verified read, then on a power-source change while null. Battery power flow is captured only when asked. Both go through an injectable `Capture`; only `ReadSource::acquire` installs the Windows one, so tests never touch Windows battery APIs.
- The HyperBoost decode gap is fixed in every mailbox lane: a confirmed read carries `DeviceCapabilities.Unavailable()` with `hyperBoostAccess: 0` (src/service_status/observed_capabilities.json, pinned to the managed codec output by the vectors).
- Live readings go in the ObserveState reply only; the subscription feed never carries them (the managed tray only takes non-null liveReadings from replies).

## Tried and ruled out
- A `let observed = ...; observed` cfg pair in readiness triggers clippy let_and_return in the lanes without the feature; the cfg'd block tail inside a StatusRequest method avoids it.

## Watch out
- With the Driver-mode read also built, the first job asks 00/84 before 0D/88; a fake that leaves 00/84 unanswered seals the Reader, and the fan pair is never sent. FanIo answers Normal for that reason.
- windows-sys SYSTEM_BATTERY_STATE uses Rust `bool` for kernel BOOLEANs, so a byte-mirroring struct is used instead.
- Every failed exchange quarantines one transaction ID. A 0D/88 that keeps failing at the tray's 2 s cadence costs IDs as fast as a failing thermal read does.
- Custom High/High with Automatic fans still gives no fresh read, so that ObserveState carries no live readings either (open; needs a managed partial-read vector).
- The standalone discovery binary also compiles performance.rs, so a new `pub(crate) use` there needs `#[allow(unused_imports)]` or clippy -D warnings fails every lane. Test-only struct literals over cfg-dependent fields need cfg on the field, not `..Default::default()` (clippy needless_update in the lane where every field is listed).
