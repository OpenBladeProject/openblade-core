---
title: Native application-profile resolution ports the unknown-external fallback
author: Jack
date: 2026-11-21
branch: feat/native-profile-resolution
touches:
  - rust/crates/openblade-domain/src/profile_set/application.rs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.NativeProfileResolutionVectors.cs
  - docs/windows-native-thermal-mutation.md
---

Supersedes 2026-11-21-native-application-profile-resolution-reads-two-jack.md where they disagree: its unknown-external claims, its claim that `desired_intent` is the predicate's desired term, and its Lease::rule reason.

## Decided
- `profile_set::application::resolve` takes an explicit `Confirmed` (managed `ConfirmedDeviceState`'s performance, fans, lighting and Custom settings; the default is all unknown). Confirmed state shapes the profile layer, so it is an input, not a recorder marker. On an `ExternalUnknown` desired source, as the state or as the projection, `Resolution::profile` is managed `CreateObservedFallbackProfile` (ProfileAutomationState.cs:554-561) with no override. Performance, fans and lighting come from confirmed state, else the saved USB-C profile. Custom settings come from confirmed state only, battery protection is `DesiredBattery`'s from the state source, and every other member is unset. `desired_intent` then gives ConfirmedFirmwareState sources for confirmed performance and fans, confirmed lighting first, no GPU tuning (ExternalUnknown is not external) and no USB-C downgrade. The `"confirmedState"` marker is gone.
- `desired_intent` is the save-path desired term only while no manual override, recovered HyperBoost intent or temporary battery override is active. A persistent Silent override over an edited rule preset leaves managed unchanged while the profile layer changes. A persistent Custom override without levels reads the profile's Custom settings even when the profile preset is not Custom (:391-398), and the legacy GPU offset move runs after the layers are chosen (:400-415). The wiring slice layers performance, fans, fan control, lighting and battery over each `Setting::source`. It takes Custom settings and GPU tuning from the raw `Resolution::profile`, layers the overrides, then reapplies `gpu_clock_settings`, and uses `with_mode` for an override's fan mode. Both helpers are public and unit-tested.
- `Lease::rule`'s retained-pair check is defence for the wiring slice, not a fix for a current caller. The battery runtime cannot change rules today: it refuses any save except a `batteryPolicy` edit (battery_runtime.rs:999-1006).
- Vector cases may set `supportsManualFansOnLimitedPower` and `confirmed`. Each update step records UpdateProfilesAsync's predicate expression (ServiceControlRuntime.cs:2116-2119) over `Resolve`. On unclassified power the runtime never runs the full transition, so its message cannot show the predicate there.

## Tried and ruled out
- Pinning `with_mode`'s Maximum speed or its unit test through vectors. In a valid saved set a fan control differs from the resolved Maximum mode only as the legacy Fixed-5400 alias, which gives the same speed. Only host overrides reach that branch, so unit tests pin it.
- Skipping the profile lighting on unknown power while confirmed lighting is known. Managed does skip it, but the selection takes the confirmed value either way, so the skip is equivalent and was dropped.
- Vector rows for Custom without levels or a profile without lighting: the validator refuses those sets. A unit test covers managed's fallbacks for them.

## Watch out
- Under unclassified power a USB-C edit changes the desired state wherever the matching confirmed member is unknown. UpdateProfilesAsync then skips the lighting restore (:2121-2122) and pauses an armed fan stop (:2154).
- The unknown-external fallback takes no USB-C Custom levels, GPU tuning or fan control: fan control is `FromLegacy` of the fallback's mode, and Custom falls to managed's default when nothing is confirmed.
