---
title: Keep ambiguous low fan trials separate from explicit recovery
author: Jack
date: 2026-11-04
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - Directory.Build.props
  - README.md
  - README.ru-RU.md
  - README.zh-CN.md
  - docs/battery-saver-low-fan-validation.md
  - docs/fan-power-source-policy.md
  - src/OpenBlade.Contracts/DeviceState.cs
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Contracts/Profiles.cs
---

## Decided
On exact RZ09-0581 BIOS 4.01 in Battery Saver on battery, the authorized 100 RPM (wire byte 01) trial acknowledged both setters but immediately returned unexpected paired mode readback. Stop all further writes, including restoration, on that ambiguity. A separate read-only session found stable Battery Saver Manual, stored 2000/2000 and measured 1900/2000. A reviewed restoration-only session repeated those stable reads and restored only the exact original 3800/3800 plus Battery Saver Automatic, with independent readback. Installed owners, protected binaries and saved intent were restored. Keep the failed trial and successful recovery separate in docs/evidence/battery-saver-100-rz09-0581-2026-11-04.json.

## Tried and ruled out
Acknowledgement is not evidence that a low fan target was retained. 100 RPM was not retained at reconciliation and was not replayed. Zero was not tested; neither zero nor one has established reserved-number semantics. The physical 1500 RPM spin-down result remains the product basis.

## Watch out
A stopped ambiguous experiment is not permission to retry or automatically restore. Reconcile read-only first; any separate restoration must bind the original saved baseline, stable current state and fresh per-native admission under existing restoration authority. Do not mark the original failed trial successful because later recovery succeeded.
