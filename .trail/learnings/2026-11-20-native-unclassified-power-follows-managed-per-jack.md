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
- Every external reading, classified or unclassified, needs fresh Windows AC presence (`Power::thermal_source`). Once the first reading is external, `PowerFence::authorize` refuses a checkpoint without presence as PowerChanged, so presence lost at a setter read or between a read and its SET stops before the next command.
- A class change between setters completes the pair when the new class is external, agrees with presence, and the permit's destination is unchanged (`Permit::destination_holds`: same desired preset for thermal; same desired fan settings and handback preset for fans; owned Stop cleanup always). Battery or lost presence stops without replay.
- Owned Stop cleanup hands back on any external class whatever source the row recorded; rows keep the recorded source.
- Unclassified power with a confirmed HyperBoost baseline, or with no presence, replies Blocked "Hardware application was not confirmed. The external power adapter could not be classified; overrides and control writes are blocked." and stores nothing, on both the thermal and the fan path.
- Only a correlated completed rejection, or a success reply with single-report geometry (`openblade_protocol::has_single_report_geometry`), maps to unclassified. Any other status, correlation, checksum, length or receive failure is no reading.
- A temporary saved unclassified records the lifecycle epoch (`thermal_storage::observe_power`, domain `temporary_after`); a classified reading at the same epoch keeps it and adopts that class, a later different class expires it.
- Status uses class -1 for storage reads when the base is not battery and Windows presence is missing (`status_query_power`), so an Unknown-power status never expires a battery temporary.

## Tried and ruled out
- Stopping on any mid-pair class change: managed completes those pairs, so it was not parity.
- Keeping the Stop cleanup gate `source != r.source`: it failed closed when an owner recorded on 3 was later classified with no Windows event.

## Watch out
- The lifecycle epoch also advances on session notices and suspend/resume, so native expires an unclassified temporary on a lock or session switch where managed (which advances only on a Windows power-kind change) keeps it.
- Native additionally requires the destination to hold across a mid-pair class change; managed does not.
- `thermal_storage.rs` is shared with the profile-storage worker; the epoch fields and `observe_power` were a minimal addition there.
