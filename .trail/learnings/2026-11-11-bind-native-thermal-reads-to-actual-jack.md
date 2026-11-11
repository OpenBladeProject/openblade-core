---
title: Bind native thermal reads to actual managed transport retirement
author: Jack
date: 2026-11-11
branch: feat/native-thermal-observation
touches:
  - build/development-deployment/WindowsNativeInventoryBurnExtractor.cs
  - docs/windows-native-origin-status.md
  - src/OpenBlade.Deployment/OpenBlade.Deployment.csproj
  - src/OpenBlade.Deployment/WindowsNativeAttemptHistory.cs
  - src/OpenBlade.Deployment/WindowsNativeEpoch.cs
  - src/OpenBlade.Deployment/WindowsNativeEpochFiles.cs
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - src/OpenBlade.Deployment/WindowsNativeTrustLifecycle.cs
  - src/OpenBlade.DeploymentBroker/BrokerNativePackageCommitmentProducer.cs
  - src/OpenBlade.DeploymentBroker/OpenBlade.DeploymentBroker.csproj
---

## Decided

The installed read-only Service composition uses the existing serialized exact RZ09-0581 paired 0D/82 getter with a typed original-generation read lease. Read freshness is private and separate from IPC caller access, strong ConfirmedPerformance, and mutation permission. Battery and no UI session are valid getter conditions; metadata-only images never acquire the mailbox. The native factory holds authenticated managed transfer and genuinely empty native owner history without inventing Retired or granting writes.

Managed acquisition reserves one shared lifetime before CreateFile and native submission across control/battery, fan RPM, retained lighting/keyboard and matrix paths. Handles, workers, detached operations and callbacks must all finish successfully. Setup verifies the original retained process exit and protected receipt, then creates one receipt/request/package-to-epoch mapping before startup. StagePayload -> PrepareService -> StopAgent -> Activate stays intact. A prior handoff-capable signed managed upgrade is required; old running versions cannot be certified retrospectively.

## Tried and ruled out

SCM Stopped, process exit, FullyDisposed, successful NativeRetirement Task status, empty disposal registry and free semaphore do not prove complete successful transport retirement. Callback work can outlive finalization. A Stored firmware setting is not a transport fault and a transport receipt says nothing about restored fans/keyboard. Matching DLL basenames are insufficient for signed Service closure authentication: use actual deps/RID resolution and hold its signed inputs, excluding unrelated UI files. Release predecessor installed-tree pins only after exit plus receipt so Activate can rename the installation. Enrollment maintenance must retain the same exclusion before its first change, refusing active managed custody rather than partially replacing binaries.

## Watch out

Only authenticated never-selected pristine bootstrap may finish creation of a missing original owner lock; selected/partial/unknown history remains fenced. Provisional native health is a temporary read denial; actual original-origin/mapping loss is sticky. Merge current metadata after worker completion before publishing a fresh thermal revision. Custom still needs CPU/GPU 0D/87. Adapter suppliers, mutations, all Service/Agent/background responsibilities, models/backends, profiles/config/journals, packaging/default cutover and physical/installed runtime gates remain unfinished. This stage permits offline compilers, static binary inspection, pure/injected tests and private current-user filesystem fixtures only; never broadly run installed::, private_pipe or actual process/token/session/SCM/task/role probes.
