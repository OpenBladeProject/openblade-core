---
title: Native low-power Driver-mode release and suspend fan handback (supersedes the Driver-mode owner learning)
author: Jack
date: 2026-11-24
branch: feat/native-low-power-driver-release
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/driver_mode_owner.rs
  - tools/openblade-windows-host/src/windows/scm/installed/low_power_cleanup.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_native.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/driver_mode_owner.rs
  - tools/openblade-windows-host/src/origin_lifetime.rs
  - docs/windows-rust-development-service.md
---

This supersedes `2026-11-23-native-keyboard-driver-mode-owner-after-jack.md` for the release admission, the epoch recapture and the low-power gap. Where the two disagree, this one holds; its other decisions (15 s cadence, L3 skip, figures and fixture numbering) stand. It also completes the "nothing captures it yet" note on `WriteScope::LowPowerFanAutomatic` in the low-power gate learning: the scope is renamed `LowPowerCleanup` and the Service's cleanup admission now captures it.

## Decided

- Every Driver-mode release and every Service fan handback holds the Service's own cleanup admission (`thermal_mutation::Cleanup`, built by `CleanupGuard::capture` in thermal_native.rs), never a console write capture. It names no console session, SID or logon, because Windows logs every user off before a shutdown reaches services and a suspend may start from the lock screen. Managed releases with nobody signed in too (`ReleaseDriverModeCoreAsync`, ServiceControlRuntime.cs:13398-13520; `FreshLightingCleanupAdmission`, WindowsVerifiedBladeDeviceProvider.cs:398-420).
- Each authorization checks fresh: origin and package trust for its scope, the epoch its scope pinned (before and after the Windows checks), a known Windows AC line status, fresh competing-controller absence (a typed denial at capture), then `ReadCustody::authorize_cleanup` (exact original custody and generation). The fan handback also needs the owner's own custody generation.
- Authority boundary: `Cleanup` is not a `Fresh` or a `Settle`. Only `DriverModePermit::release` (00/04:0000 after a fresh 00/84 read of Driver) and `FanHandbackPermit` (journal stages 31, 40-43, 50, 51 only; 51 not while pending; no request, preparation, resolution or no-op confirmation) hold it. The lane runs them only through `mutate_cleanup_in`, which requires the permit's admission to equal the custody's cleanup admission of that scope; journal rows go through `journal_replace_cleanup(scope, ...)`.
- `CleanupScope::OwnedStop` pins `WriteScope::Ordinary` (refused in low power, as managed blocks Stop cleanup there). `CleanupScope::LowPower { generation }` pins `WriteScope::LowPowerCleanup { generation }`, admitted only while that exact suspend generation is current.
- The console-capture epoch recapture (E1, E7) is gone: the release no longer needs the acquire capture to follow the epoch.
- Low power (`low_power_cleanup.rs`): once per suspend generation, within managed's 1.5 s `MaximumAge` of the notice's QPC stamp, keyboard release then fan handback. Keyboard share = min(remaining - 600 ms, 900 ms); the fans run to the window's end. A new suspend generation wakes the origin tick (`Lifecycle::suspended`, a `tokio::sync::Notify`). Fast Startup is treated as this path. In low power the 15 s step does nothing, so reacquire waits for a resume or DisplayOn.
- Only `Submitted`/`Acknowledged` set `uncertain`, which ends every later release, acquire and maintain write in the process. A dropped release job stays in `Keyboard::inflight` and is read by the next release after it drains active work.
- D1 decision: the stricter epoch fence is kept. A lock or power-status notice during a cleanup job ends that job at its next authorization; it is not recaptured.
- Divergence from managed, deliberate per the slice brief: managed's suspend fan stop restores only a Battery Saver fan stop it owns (`OwnsBatterySaverFanStop`, ServiceControlRuntime.cs:13086-13101). Native hands ordinary owned manual fans back at suspend.

## Tried and ruled out

- Capturing the cleanup admission at the release under the console capture's epoch: it refuses whenever a lock or logoff moved the epoch, which is the shutdown case itself.
- Checking `verify_retained_failure` in `Runtime::capture_cleanup` before checking that the factory is composed broke `edge_original_owner_integrity_loss...` (the uncomposed path must return None first).
- Mutants that survive, with reasons: allowing stage 30 alone in `FanHandbackPermit::record_fan` (the journal's transition rule refuses it anyway; allowing every stage is caught); dropping the `release.swap` Attempted check in `hand_back_fans` (a registered job either journaled a pending row, which returns Unsettled first, or confirmed, which clears `owned`); dropping the `51 && pending` check (the journal fences it). `CleanupGuard::admit` and `ReadLease::authorize_cleanup` are compile-only offline, like `WindowsGuard`; the test models (`CleanupFresh`, the test custody) mirror them.

## Watch out

- `fatal_evidence_from_the_lane_fails_the_release` now walks 17 integrity checks before the release's first send; the 18th follows the baseline query.
- `handback_permit_for_tests` needs the origin stopped for the owned-Stop scope, because owned-Stop trust requires it.
- `PinnedFresh` lacks `power_generation`, so fan requests fail under it; use `scoped_fresh` in fan-owner tests.
- Remaining before packaging: attended timing of a real release and handback at suspend and shutdown on RZ09-0581, observing that Fast Startup delivers PBT_APMSUSPEND to the native Service, the 6a-5 durable owned and uncertain record, and physical validation. The 150 ms keyboard entry can be spent by an in-flight request; the fans then still run. The figures (keyboard about 0.28 s plus three exchanges, fans about 0.55-0.6 s) are estimates.
