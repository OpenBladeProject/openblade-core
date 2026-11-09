---
title: Gate Stop Auto control on recovery and preserve confirmed ownership
author: Jack
date: 2026-11-06
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - Directory.Build.props
  - docs/so6120-supervised-fan-stop-trial.md
  - src/OpenBlade.Contracts/DeviceState.cs
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Contracts/ServiceMessages.cs
  - src/OpenBlade.Protocol/ProductionBatteryFactory.cs
  - src/OpenBlade.Service/ControlRequestHandler.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/ServiceStatusSnapshotComparer.cs
---

## Decided
Keep the new typed Stop/Auto service controller and tray row gated behind an unavailable production backend. Exact RZ09-0581 trial evidence is display metadata only; the repeated USB-C Balanced zero-duty experiment does not establish host-loss recovery. Stop is transient, never a persisted profile value. Auto is a generic owned handback independent of the Stop entry profile and temperature limits. Preserve saved normal fan intent while displaying confirmed Stop, Auto, or recovery separately.

## Tried and ruled out
The pinned SO6120 FE branch bypasses normal feedback; the inspected watchdog and lifecycle paths do not establish a host lease. InpOut close/unload does not restore Auto. A second writer cannot recover a pending ambiguous mailbox operation. A surviving sole-owner guardian may cover caller loss but cannot guarantee recovery from its own crash, native hangs or OS failure. No production backend or restart takeover is admitted.

## Watch out
Mapped A0 observations are commands: require fresh basic device/controller/power/session/transaction admission and fence them after ambiguity. Confirmation after a resolved write must survive caller cancellation, require independent state readback and confirmed ownership for Stop, and only then permit owned Auto cleanup. Preserve the last accepted toggle state on uncertainty. Status-publication failures must never strand the service operation gate; the recovery block must cover keyboard and brightness-release bypasses as well as ordinary controls. Use injected backends and disconnected renders for these changes; no hardware command or installation follows from implementing the UI.
