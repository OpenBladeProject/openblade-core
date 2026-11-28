---
title: Recover native origin status through enforced original attempt custody
author: Jack
date: 2026-11-11
branch: fix/native-origin-status-corrections
touches:
  - src/OpenBlade.Deployment/WindowsNativeEpoch.cs
  - src/OpenBlade.Deployment/WindowsNativeEpochFiles.cs
  - src/OpenBlade.Deployment/WindowsNativeInstalledSelection.cs
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - src/OpenBlade.Deployment/WindowsNativeTrustLifecycle.cs
  - src/OpenBlade.DevelopmentDeployment.Setup/DevelopmentDeploymentProvisioner.cs
  - src/OpenBlade.Setup/SetupCommandRunner.cs
  - src/OpenBlade.Setup/SetupNativePackageTransaction.cs
  - src/OpenBlade.Setup/WindowsSetupPlatform.cs
  - tests/OpenBlade.Deployment.Tests/OpenBlade.Deployment.Tests.csproj
---

## Decided
New signed native Service artifacts advertise startup-custody-v1. Setup writes origin-bound submission before service mutation. Every native Service boot/restart holds the same per-epoch exclusive custody, rechecks selection after acquisition, and durably records exact original launch before dispatcher/factory. Actual metadata-worker/listener drain binds the exact launch digest. Setup stopped/interrupted recovery validates all retained launch/drain pairs under custody and separately preserves unknown/active firmware owner fences. Verified bootstrap is re-admitted under Begin's writer; null current never resets reservations or mappings. Native failures after possible predecessor retirement preserve explicit recovery instead of generic service restoration. ProgramData metadata-only compatibility, early/fresh trust preflight plus legacy-parent pinning, typed peer availability and owner-v2 exact UTF16 paths are shared fixes.

## Tried and ruled out
SCM Stopped, absent PID and process exit are not drain certificates. A no-launch proof is valid only for this enforced signed metadata-only entry recipe under the same custody; legacy launchless artifacts stay unresolved unless the original live stop/drain route proves retirement. No writer-order or skipped STOP drain defect was established. Static CRT guard remains. Trail phrase conflicts about directory sharing, restricted-token scratch access, path-only admission and READY lines do not apply: sharing/ACL primitives remain strict, no role/probe is launched, startup identity still binds complete signed package+original process, and typed readiness/drain are retained.

## Watch out
Future firmware composition needs a separately reviewed custody/drain protocol: metadata retirement cannot clear firmware ownership. Unknown/partial launch, submission or owner history stays preserved and denied. Canonical owner v2 is UTF16LE/base64; legacy v1's final field remains literal ASCII. Tests/builds are offline evidence, not live Windows or hardware proof. Required Rust core and default managed behavior stay enabled. This supersedes only the validated origin/status causes at d9668635, not prior authority boundaries.
