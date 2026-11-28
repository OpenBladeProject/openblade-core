---
title: "Native firmware versions: managed record, McuVersion left Unavailable"
author: Jack
date: 2026-11-23
branch: feat/native-firmware-versions
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/firmware_versions.rs
  - tools/openblade-windows-host/src/windows/scm/installed/firmware_versions_windows.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/tests/data/firmware-version-vectors.json
---

## Decided
- Native firmware versions ride `service-live-readings-v1` (no new feature, so packaging needs no change): managed captures `FirmwareVersions` once at runtime start and never refreshes it, so native captures once per acquired read source, on a worker thread that the first status publication starts. The read job never waits for it and spends none of its HID budget. A status before the worker finishes has no `firmware` member; the record is published when the worker ends or after 10 s, with parts still unread as `Unavailable`.
- The record has no null text. A failed or missing part is the managed text `Unavailable`, as managed writes it, and the two accesses are null except on the exact RZ09-0581 (`ReadOnly`, 1). The member is device-independent status like battery health: it is in every projection and every ObserveState reply whatever mask the request carries, and it is copied from the published active status after `decorate` in `observe_at`, so a reply at revision N carries what publication N carries.
- McuVersion is always `Unavailable`. Managed reads it with `IBladeDevice.ReadMcuVersionAsync`, a Razer firmware query over the control interface, after the first verified observation (three attempts). Windows has no source for it. This is the one field left for a reviewed native MCU query; do not infer it from another source.
- The touchpad is not a feature report. Managed opens every HID interface with access 0, share read and write, no flags, and reads `HidD_GetAttributes` version, preparsed data and caps; the version is BCD. Native does that only for interfaces whose devnode carries the hardware ID `HIDVID_093A&UP:000D_U:0005`. The RZ09-0581 touchpad is HID over I2C, so its interface path does not name its vendor or product, and native never opens the laptop control collections.
- Only the exact RZ09-0581 reads Thunderbolt and the touchpad, after the model from `SystemSKU` is mapped by the managed catalog prefix rule (the inventory fixture is the catalog, 22 models in order).
- Parity is pinned by `firmware-version-vectors.json`: 81 managed `WindowsFirmwareVersionSource` scenarios through the real v85 codec, plus `Version.TryParse` and `Convert.ToInt32` rows. The managed registry loop and USB4 enumeration filter are private, so the managed test repeats their rules; the Rust side applies them in pure functions.

## Tried and ruled out
- Hand-written `Version.TryParse` and `int.Parse` first missed .NET accepting trailing NULs and leading `+` signs; the managed rows caught it. Keep those rows when touching `parse_int32`.
- Three Thunderbolt candidate conditions (hardware IDs, service, parent present) are implied by the exact match, so only the firmware version needs its own check. A mutant removing them survived as an equivalent, so the code dropped them.
- Mutants: 49 over parsing, selection, the exact-model gate, the deadline, caching and publication; every non-equivalent mutant was killed (one `1 << 33` overflow bound is equivalent, three redundant-presence mutants led to the simplification above, and removing the overflow guard needed a long-digit-string test).

## Watch out
- `lane 4` (`service-profiles-v1`) once failed `tfc_release_a_restored_cancel_keeps_its_reservation_from_another_writer` under load with an unrelated left 0 / right 1 count, and passed twice alone and in the next full run. It does not compile the new module.
- The managed vector test regenerates with `OPENBLADE_REGENERATE_NATIVE_FIRMWARE_VERSION_VECTORS` set to a directory holding a copy of the file; the Rust frames regenerate with `OPENBLADE_REGENERATE_NATIVE_FIRMWARE_FRAMES`. Building `OpenBlade.Service.Tests` needs `rustc` on PATH for the Core.Native step, so run `dotnet test` from PowerShell with the cargo env set.
- The test thread worker is real, so schedule tests pass a `now` to `Host::poll` instead of using a paused clock.
