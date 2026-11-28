---
title: Recheck Windows controller absence at each simulated setter
author: Jack
date: 2026-11-10
branch: feat/windows-controller-admission
touches:
  - rust/crates/openblade-platform-windows/src/controller_presence.rs
  - rust/crates/openblade-platform-windows/src/controller_presence/admission.rs
  - tools/openblade-windows-host/src/performance_runtime/device.rs
  - tools/openblade-windows-host/src/fan_runtime/device.rs
---

## Decided
The opt-in Windows controller admission consumer requests a new bounded process and query-only SCM observation. The RZ09-0581 performance and ordinary fan replays share a sticky controller-clear gate and recheck it at entry and immediately before each simulated setter. A lost check after any submission preserves the existing uncertain and read-only outcome. Controller absence cannot restore identity, session, power, or HID lease admission.

## Tried and ruled out
At 327d57a1245785f91ea63c61790adc5568347ce9, WindowsControllerProcessParents.cs:83-141 requires the canonical helper location and exact version metadata. SynapseBladeBlockerVerifier.cs:25-145 binds patch exclusions to the executable and archive hashes, marker, and process start. WindowsLampArrayInterfaceSource.cs:27-112 enumerates present, active HID interfaces using interface properties and requires usage 0059/0001. The exact control collection metadata lease in discovery/hid_lease/windows.rs:33-96 cannot establish complete managed-equivalent LampArray coverage. Keep LampArray unknown and leave VerifiedImage unset on native rows. The classifier continues to retain the helper as a competing controller.

## Watch out
Process and SCM reads are point-in-time evidence without an atomic cross-source snapshot. The consumer discards samples outside the current request or over the one-second request budget. Default fixtures leave the consumer disabled. Native acquisition is compile-checked only. Preserve the separate readback gate and submission status on controller refusal.
