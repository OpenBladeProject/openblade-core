---
title: Native setColorProfile never restores against an unidentified readback
author: Jack
date: 2026-11-19
branch: feat/native-agent-manual-brightness
cost: { minutes: 28, tokens: 179017 }
touches:
  - captures/
  - rust/crates/openblade-platform-windows/src/brightness/tests.rs
  - tools/openblade-windows-host/src/windows/scm/installed/battery_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/native_package/tests.rs
  - tests/OpenBlade.Deployment.Tests/OpenBlade.Deployment.Tests.csproj
  - tests/OpenBlade.Deployment.Tests/WindowsNativeEpochTests.cs
  - tools/openblade-windows-host/tests/data/native-api-set-imports-v1.json
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
---

## Decided
- Native setColorProfile never restores against a readback that does not re-identify the panel. color_profile::write splits readback failures into two types: Readback {Read, Mismatch} may run managed Restore; Unverified {Topology, NoInternalDisplay, MultipleInternalDisplays, PanelChanged, HdrUnknown} never restores and the host reports Indeterminate, pausing color. Transaction::reidentify() runs before any profile read of the final observation. Managed restores in these cases; review measured 4,176 of 5,601 such cases where that would have written to an address the readback could not tie to the panel, while all setters had already been read back.
- HDR on at the final readback still restores, as managed does (known state on the same panel).
- The fence refuses HDR on and HDR unknown before any submission. The shared fence sequence is display/fence.rs admit_submission; each writer maps FenceRefusal through its own From impl.
- The Unverified reply keeps a reason clause (topology status, no internal panel, several panels, panel changed during the change, HDR unknown). The Tray only traces this string (DisplaySettingsViewModel "Display settings operation reported"), so it is the only diagnostic record of why native could not verify.
- The frame is manual_write (formerly ccd_write); the earlier color learning's ccd_write naming and its "readback requires the same PanelIdentity" summary are superseded by the rules above.

## Tried and ruled out
- Restoring after a Topology or NoInternalDisplay readback for managed parity: the prepared adapter/source may denote another monitor, and the scope setter keys by device while reads key by address, so a stale restore can cross-wire them.

## Watch out
- Residual parity cost: if Windows changes the endpoints but the device path is the same physical panel, native reports Indeterminate until the next display read where managed would confirm.
- The .obid identity bound is now 4096 bytes with an unchanged record format, so the next Agent capability (applyDisplayProfile) fits: the worst composition is 977 bytes. See 2026-11-19-native-build-identity-bound-and-the-jack.md.
