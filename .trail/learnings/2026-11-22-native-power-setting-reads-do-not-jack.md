---
title: Native power-setting reads do not fence and register after RUNNING
author: Jack
date: 2026-11-22
branch: feat/native-power-setting-notifications
touches:
  - docs/windows-native-thermal-mutation.md
  - tests/OpenBlade.Service.Tests/PowerSettingNativeVectorsTests.cs
  - rust/crates/openblade-domain/src/power_setting.rs
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/src/origin_lifetime.rs
  - tools/openblade-windows-host/src/scm_host.rs
  - tools/openblade-windows-host/src/windows/scm/installed/native.rs
  - tools/openblade-windows-host/src/windows/scm/installed/power_notifications.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/temporary_charge_release.rs
  - tools/openblade-windows-host/tests/data/power-setting-vectors.json
---

## Decided
- A power-setting read (POWEREVENT with PBT_POWERSETTINGCHANGE) does not advance the lifecycle epoch. Managed fences nothing on display or AC/DC reads (ServiceLifecycleRefreshLoop.cs:97-158). Fencing them made every display or AC/DC notice abandon in-flight work, and a temporary-full-charge cancel on battery taken that way was never retried. The read still updates the display state and can still signal DisplayOn. Suspend, resume, other power events and session changes still fence. `origin_lifetime::fences` is the one decision, and `notice` is the entry the installed handler uses; `record` is now test-only.
- `installed/power_notifications.rs::Supplier` registers right after SERVICE_RUNNING is reported and unregisters before SERVICE_STOPPED. SCM drops power events the current status does not accept, and START_PENDING accepts none, so registering during startup could lose the initial value Windows sends at registration. A registration failure fails the RUNNING report, so the Service stops as failed.
- An unregistration failure at stop does not change the stop's status. Managed rethrows it only at host disposal, after STOPPED was reported, and setup's failure flag with restart actions would otherwise restart an intentional stop. The Service has no native diagnostics sink, so `take_cleanup_failure` is dropped in `native.rs`; wire it when one exists.
- `power-setting-vectors.json` mapping rows come from the real NativeSubscription callbacks on an uninitialized instance, routed by GUID as Windows routes them.
- C1 automatic transitions need no time debounce. Managed coalesces into one pending PowerChanged bit and keys on the power-kind generation (`power_generation` natively).

## Supersedes
- The B1 learning `2026-11-22-native-low-power-gate-replaces-the-jack.md` says "`record(code, signal, timestamp)` is the entry B2 uses" and that the lifecycle worker's `Activity` "must read this gate before the worker is wired". B2 uses `notice`, and the worker was retired, not wired.
- The B2 learning `2026-11-22-native-power-setting-notifications-feed-the-jack.md` says each display and AC/DC notice advances the epoch and shows up "as more retried captures". It does not advance the epoch, and abandoned work was not always retried: the temporary-full-charge cancel was lost.
- The B2 learning's "Unregistration runs inside the report closure when the status is SERVICE_STOPPED ... Registration failure stops the Service as failed" before startup: registration now follows the RUNNING report, and a cleanup failure no longer becomes ERROR_GEN_FAILURE.
- `2026-11-10-coalesce-inactive-rust-windows-lifecycle-evidence-jack.md` describes the worker in `windows/scm/lifecycle.rs` that coalesced invalidations. That worker was deleted; only `observe()`/`Observation`/`PowerSource` remain there.

## Tried and ruled out
- Keeping the fence and retrying the battery event. Managed has no generation for these reads, so a fence there is a native-only behavior with nothing to match.
- Proving the copy bound through `decode`: it rechecks DataLength. A guard-page test (a buffer ending at a PAGE_NOACCESS page, test-only `Win32_System_Memory` dev-dependency) faults on any read past the declared payload.

## Watch out
- A genuine second PBT_APMPOWERSTATUSCHANGE between the status-source tick's epoch read and the capture's authorize can still lose the one-shot temporary-full-charge cancel: `battery_source_event` marks it abandoned, then fails the epoch check. That predates B2 and still needs a fix in the battery owner.
- The `control()` forwarding test binds the process-global SCM ingress. Keep it the only test that calls `set_ingress` in each test binary.
- Managed `OnConsoleDisplayNotification` does `checked((int)current)`, but `TryReadPowerSettingDword` refuses values above 2 first, so it cannot overflow.
- C2 needs the display-off transition that `power_event_signal` drops; C3 needs managed's resume dispatch on DisplayOn when the gate was set (WindowsServiceLifecycleEventSource.cs:363-368).
