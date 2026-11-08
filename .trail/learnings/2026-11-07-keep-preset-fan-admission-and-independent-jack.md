---
title: Keep preset fan admission and independent lighting intent exact
author: Jack
date: 2026-11-07
branch: feat/blade-firmware-model-support
touches:
  - src/OpenBlade.Protocol/ExperimentalBladePort.Fans.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.IndependentLighting.cs
  - src/OpenBlade.Service/ServiceControlRuntime.IndependentLighting.cs
  - src/OpenBlade.Contracts/Profiles.cs
  - docs/bios-ec-source-validation.md
  - tests/Build.Tests/ReadmeHardwareSupport.Tests.ps1
---

## Decided
Product 581 / RZ09-0301 / 1532:0245 admits independent absolute keyboard brightness and profile-0 logo state/effect, plus Balanced manual targets on full-power AC. Its old matrix effect setter has no reviewed matching restorable getter. Keep writable effects empty and the twelve discovered UI effects observed-only. The additional immutable source review is evidence/source-reviewed-blades/2026-11-07-rz09-0301-controls.json; preserve the original admission as history.

Fan metadata 0D/83 identifies a current range but not the preset. Intersect it with the 3700..5300 step-100 envelope, publish confirmed bounds to shared capabilities, and recheck paired preset/manual flags after metadata before every setter. Reject invalid preset-ranged Fixed/Curve changes without falling through to Automatic/Balanced bridges. Flag-only Automatic handback needs no target/range query; strict recovery and target writes retain their independent target checks. Apply those shared corrections wherever the same preset-ranged contract exists.

Independent lighting requests persist merged intent but dispatch and confirm only the edited fields. A logo edit must not reapply an earlier brightness target or capture a synthetic effect/brightness. Keep observations separate from the saved keyboard-effect wrapper; null independent intent means unmanaged and no new writes. Suppression changes only explicitly managed brightness. New IPC and profile/override/export schemas require coordinated deployment and explicit legacy migration; old envelopes cannot smuggle new write intent.

A read-only recovery must retain the lighting hold across suppression changes and profile updates. ShouldYieldAutomaticLighting must honor that hold even under AlwaysOverrideExternalLightingChanges. For independent controls, compare IndependentControls when deciding whether a profile edit is a new hardware request; effect/RGB/legacy-brightness wrapper changes do not release it. Failed or canceled UI requests invalidate queued commit generations, so a later resume cannot replay a stale edit.

## Tried and ruled out
Do not borrow modern 0F/82 or invent 03/8A for the old matrix. Product 581 does not enable the shared battery-optimizer path. Shared helpers and a vendor effect catalog do not establish exact-model write authority. The UI uses the existing brightness/logo rows and shows unknown values without a fabricated percentage or selected logo.

## Watch out
The RZ09-0528 follow-up now traces RV32+C EC ingress, descriptors and consumers for modes, targets, Custom policy, charge ceiling and version publication. It supersedes the earlier bounded-review note's unresolved internal-chain status, while the full 1532:02C6 MCU/HID-to-EC bridge remains unproved. EC defaults of 5100 RPM and an internal maximum-fan literal of 5500 do not expand the admitted 5000-RPM product cap. Static bytes do not prove active tables, measured watts/RPM, physical charging behavior or durable hardware restoration.

Build contract tests must follow the supported-model list and guide link semantically; do not require obsolete README disclaimer sentences. Source hashes/excerpts, software tests and disconnected renders are not physical validation. No hardware write, driver installation or firmware execution was performed in this work.
