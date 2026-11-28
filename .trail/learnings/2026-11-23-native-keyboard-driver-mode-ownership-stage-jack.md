---
title: Native keyboard Driver-mode ownership (stage 6a-3)
author: Jack
date: 2026-11-23
branch: feat/native-driver-mode-acquire
---

## Decided

- Stage 6a-3 adds `service-keyboard-driver-mode-v1` to the native Service (RZ09-0581 only): acquire and maintain keyboard Driver mode on a 15 s step of the 500 ms origin tick, after `process_power_source`, and restore Normal at shutdown before `stop_fans`. Owner code is `windows/scm/installed/driver_mode_owner.rs`; the write recipe is `Reader::apply_device_mode` in `discovery/hid_lease/performance/device_mode.rs`.
- Write admission is the console user's ordinary capture (`console_pair`, the same `lifecycle_pair` the lifecycle tick uses). No Service-scoped write admission was added. With nobody signed in or a locked session the step is refused, keeps the last confirmed status and retries at the next step. This deliberately differs from managed, which maintains Driver mode with no user.
- An Indeterminate setter (submitted, no exact ack plus independent 00/84 readback) stops the owner for the process: publish null, never replay. Managed recovers on a fresh port and retries; native is stricter on purpose.
- Ownership is in memory only. Native restores Normal only for a Driver mode it claimed; managed `ReleaseDriverModeCoreAsync` writes Normal whenever it reads Driver.
- The 00/04 setters (`DeviceModeSet`) are behind the protocol feature `device-mode-setters`, which only `service-keyboard-driver-mode-v1` enables. Both package readers (Rust `native_package.rs`, C# `WindowsNativePackage.cs`) refuse the feature and the marker by name, before the features it implies.

## Tried and ruled out

- Publishing the step result through the subscription feed. The feed is projected from the metadata base and is never device-verified, so `keyboardDriverModeActive` there is always null and a verified-feed check means nothing is ever sent. Use the retained read (`ReadSource::driver_mode_verified`) to decide, and publish through the read cadence (`record_driver_mode` / `forget_driver_mode`).
- Testing the locked-console refusal by changing the session id: the rig probe follows `current_session`. Use `scoped_fresh` with `current_console = false`.

## Watch out

- Deferred, and it blocks packaging admission: the low-power boundary release of Driver mode and the D1 suspend fan handback are one future slice. Today the Service only refrains from acquiring or maintaining while the low-power gate is set; it does not restore Normal at a low-power boundary.
- The setter and readback transaction IDs are not journaled until 6a-5, so the journal seed does not cover them. A stale reply fails closed only because both the exact ack (ID, class, command, status, checksum, byte echo) and an independent readback on a different ID must pass.
- Durable owned-or-uncertain recovery is 6a-5. A crash while owning leaves Driver mode on, and the next process will not claim it.
- Mutation survivors on the release path are equivalent: `released` blocks a second release independently of `release_uncertain`, and `mutate_cleanup` repeats the owned-Stop admission the owner checks first.
- In C# packaging tests, a forged feature list must stay sorted and distinct; otherwise the reader fails earlier with "features do not admit the selected role".
