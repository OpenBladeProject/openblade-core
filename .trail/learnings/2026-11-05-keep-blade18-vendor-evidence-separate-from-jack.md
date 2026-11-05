---
title: Keep Blade18 vendor evidence separate from confirmed performance no-ops
author: Jack
date: 2026-11-05
branch: feat/blade18-source-support
touches:
  - Directory.Build.props
  - docs/blade18-source-feature-recovery.md
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - tests/OpenBlade.Protocol.Tests/Blade18FixedFanTests.cs
  - docs/blade18-vendor-support-audit.md
---

## Decided

For issue #739, keep the exact RZ09-0484 vendor contract separate from physical recovery. The November 5 current Product672 entry changed filename/build, but its exact product descriptor/runtime data and shared command class remain unchanged. Official BIOS2.09 and EC1.07/MCU1.04 packages supply pinned images and updater applicability; initial static parsing did not recover a new runtime control contract or standalone CPU-temperature mapping. See docs/blade18-vendor-support-audit.md for source pins.

A confirmed identical performance preset is a no-op after fresh admission and complete baseline validation. Return before different-preset manual-fan restrictions, preserving existing mode/targets without setters. This fixes02A0 Balanced/Fixed and Silent/Fixed requests and is tested on shared029F/029D paths too. HyperBoost cannot match an ordinary preset; Custom writes and actual transitions retain their existing cleanup/admission.

## Tried and ruled out

Do not infer a new model capability merely from a new Synapse bundle hash, a shared API method or a firmware flashing payload. The old owner failures precede PR826, and no post-nightly97 owner diagnostic exists in issue739. They do not prove the new no-op bug caused those failures, nor that source/tests establish hardware recovery.

## Watch out

Shared keyboard filter ABI compatibility and exact-model Fn-state/primary-mode semantics are separate evidence. Prefer a targeted ordinary diagnostic export for a current failure; another broad Observer matrix does not establish missing service admission state, CPU telemetry or legacy THX endpoint binding. Never overwrite the captured BIOS provenance with an invented production minimum.
