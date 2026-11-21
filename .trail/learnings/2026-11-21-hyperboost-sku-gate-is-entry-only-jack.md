---
title: HyperBoost SKU gate is entry-only; battery exit hands off to Balanced
author: Jack
date: 2026-11-21
branch: feat/rz09-0510-hyperboost
touches:
  - CHANGELOG.md
  - docs/experimental-blade-support.md
  - docs/experimental-hyperboost-evidence.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/zh-CN/experimental-blade-support.md
  - evidence/source-reviewed-blades/2026-11-20-rz09-0510-hyperboost.json
  - src/OpenBlade.Contracts/DeviceState.cs
  - src/OpenBlade.Contracts/HyperBoostCapabilityPolicy.cs
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Protocol/ExperimentalBladeFactory.cs
---

## Decided
- The vendor HyperBoost SKU recheck (029F, 02B7) declines entry only. It is carried as `DeviceCapabilities.HyperBoostEntryRestriction` (IPC 86); `HyperBoostAccess` and the restore bases stay ReadWrite, so user exit, battery exit, low-power, display-off, shutdown and journal recovery never depend on the SKU. Entry is refused by the policy entry bases, the service (with `HyperBoostEntryRestrictionMessage`) and the port (`EntryRestrictedMessage`).
- A missing or short SKU admits entry, as in Synapse: `charAt(9)` returns "" there. Only a positively read R, S, 5 or 6 restricts.
- The tray's `CanExit` is `IsWritable(capabilities with { HyperBoostEntryRestriction = null })`, not just `HyperBoostAccess == ReadWrite`: an active HyperBoost with no admitted base set must stay read-only (existing test `ActiveHyperBoostWithoutPadlessAdmission_RemainsPresentedReadOnly`).
- Every mode-7 battery exit (029F, 029D, 02B7) sends the Balanced handoff raw 6 whatever base is owned. `IsRestoredBase` accepts Balanced or BatteryBalanced only when the exit source is Battery; on AC it stays strict. The service journal check (`ConfirmedStateMatchesHyperBoostTransitionBaseline`) uses it with `journal.RestorePowerSource`.
- The exit baseline admits HyperBoost with Fixed fans (Synapse's setter writes [1,sel,mode,0] from any state); readback must confirm Automatic.

## Tried and ruled out
- Making `IsRestoredBase` strictly Balanced on battery: it breaks the validated 0528/0581 backends, which restore the owned base on battery. The lenient OR form is deliberate.
- Building IPC validation test frames with `JsonSerializer.Serialize` defaults: PascalCase names are rejected as unmapped members before capability validation, so those tests were vacuous. Write frames with `IpcMessageCodec.WriteServerFrameAsync`, mutate camelCase members, assert the exact message.

## Watch out
- The service test fake only applies the port's battery rule when `HyperBoostExitPowerSource` is set; `ReportsBatteryBalancedForBalancedExit` alone reports BatteryBalanced on AC too.
- A battery exit whose journal clear fails leaves the journal Relinquished, not RestorePending, so it never reaches `RecoverHyperBoostRestoreAsync`; test the transition check with a constructed RestorePending journal.
- The real-provider test Io does not model the adapter reading, so a battery HyperBoost exit there fails with "exit adapter reading disagrees"; battery handoff is covered at the port.
