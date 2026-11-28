---
title: Bind eager native origin and public status verification without firmware admission
author: Codex
date: 2026-11-11
branch: feat/rust-native-origin-integration
touches:
  - src/OpenBlade.Deployment/WindowsNativeEpoch.cs
  - src/OpenBlade.Deployment/WindowsNativeEpochFiles.cs
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - src/OpenBlade.DeploymentBroker/BrokerEnrollment.cs
  - src/OpenBlade.DeploymentBroker/BrokerNativePackageCommitmentProducer.cs
  - src/OpenBlade.DeploymentBroker/OpenBlade.DeploymentBroker.csproj
  - src/OpenBlade.DevelopmentDeployment.Setup/DevelopmentDeploymentProvisioner.cs
  - src/OpenBlade.Setup/OpenBlade.Setup.csproj
  - src/OpenBlade.Setup/Program.cs
  - src/OpenBlade.Setup/SetupNativePackageTransaction.cs
---

## Decided
Keep public native verification outside the private OpenBlade machine-data ancestor. Bind a minimal same-held enrollment projection in package v2 and retain the original signed package, full artifact mapping and monotonic frontier. Capture loaded origin before dispatcher/factory. Provisional status readiness is original-Setup-bound; committed boot has no live Setup dependency. Missing actual adapter/activity/managed-drain sources deny firmware while metadata and readonly Service subscriptions remain reachable.

## Tried and ruled out
A Users-readable child under a private parent does not solve generic-read ancestor access. A copied broker key without explicit selection retirement cannot detect unenrollment. SCM Running or process exit is not a worker-drain certificate. Static CRT removes the unprovisioned VCRUNTIME dependency; import-name allowance alone is insufficient. Do not relocate old stores or reset owner/mapping history.

## Watch out
The native status build is explicitly LocalDev-only and managed remains the default. Status-drain acknowledgement is separate from firmware-owner retirement. Require exact original-process binding and durable unresolved fences through cleanup. The first native firmware takeover still needs a genuine managed handoff; do not fabricate Admission or clear state from AC/console ID. Offline source/tests/builds do not prove installed access or kernel/hardware behavior. Full Service/Agent/background/model/cutover parity remains.
