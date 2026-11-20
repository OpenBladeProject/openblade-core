---
title: Native unclassified power follows managed per-SET admission
author: Jack
date: 2026-11-20
branch: feat/native-unclassified-external-thermal
touches:
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
  - rust/crates/openblade-domain/src/automation.rs
  - docs/windows-native-thermal-mutation.md
---

Supersedes 2026-11-20-native-thermal-control-on-unclassified-external-jack.md where they disagree. Native thermal and fan control on an unclassified 07/8C reading (RZ09-0581, managed f6502f85) now follows managed `AdmitOrdinaryThermalCommandAsync`, not a class-stability rule.

## Decided
- An unclassified reading needs fresh Windows AC presence. A performance setter needs it on every external class, and so does a fan return to Automatic that carries a saved preset (managed applies it through `SetPerformanceAsync`; `PowerFence::require_presence` refuses it before Prepared). Any other fan setter on a classified class needs Windows AC that agrees, or Windows battery, where managed admits fan commands unchecked (`allowBattery`); unknown Windows power admits neither (`Power::thermal_source`, `PowerFence::source`). The presence a reading was admitted with must hold at every later checkpoint, so `PowerFence::authorize` refuses a checkpoint without it as PowerChanged.
- A class change between setters completes the pair when the new class is external, admitted, and the permit's destination is unchanged (`Permit::destination_holds`: same desired preset and no incomplete GPU effect for thermal; same desired fan settings and handback preset for fans; owned Stop cleanup and the synthesized unclassified-clear handback always). Battery or lost presence stops without replay.
- Two transitions pin the class of the operation's first reading (`PowerFence::pin_class`), after managed: a direct HyperBoost exit to Performance (`requireExternalPowerOnDirectHyperBoostExit`), and a fan transition that writes the Performance state pair without a carried preset, owned Stop included (acPreset with `EnsureThermalWritePowerAsync`). A target-only change under manual fans and Balanced pin nothing, since managed has no acPreset there.
- Owned Stop cleanup hands back on any external class whatever source the row recorded; rows keep the recorded source.
- When the operation's first reading is unclassified, a confirmed HyperBoost baseline or missing presence replies Blocked "Hardware application was not confirmed. The external power adapter could not be classified; overrides and control writes are blocked." and stores nothing, on both the thermal and the fan path.
- Only a correlated completed rejection, or a success reply with single-report geometry (`openblade_protocol::has_single_report_geometry`), maps to unclassified. Any other status, correlation, checksum, length or receive failure is no reading.
- A temporary saved unclassified records the Windows power generation (`Lifecycle::observe_power_kind`, `thermal_storage::observe_power`, domain `temporary_after`); a classified reading in the same generation keeps it and adopts that class, a later different class expires it. An unknown ACLineStatus is a failed capture (`power_kind` returns None), never a kind.
- Status reads storage at class -1 unless fresh Windows power agrees with the status base (`status_query_power`: 1 only with Windows battery, the base only with Windows AC).

## Tried and ruled out
- Stopping on every mid-pair class change: managed completes those pairs unless a class pin applies.
- Keeping the Stop cleanup gate `source != r.source`: it failed closed when an owner recorded on 3 was later classified with no Windows event.

## Watch out
- Native additionally requires the destination to hold across a mid-pair class change; managed does not.
- Native also checks a pin at the first setter (0 writes, Stop leaves 31 pending), where managed skips it on its first command (1 write, Indeterminate).
- Native needs a reading's admitted presence at every later checkpoint; managed only until that command's own SET, so a Performance entry admitted on AC that later sees Windows battery completes in managed and stops in native.
- Under Windows battery managed's fan commands skip the adapter query; native still reads it and stops on a battery or unclassified reading. A Balanced HyperBoost exit re-reads per setter in native and reads once in managed.
- A Windows kind change is seen only while a sample (SCM power notice, request resolution or destination check) still shows it.
- `thermal_storage.rs` is shared with the profile-storage worker; the generation fields and `observe_power` were a minimal addition there.
