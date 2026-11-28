---
title: "Native firmware versions: I2C touchpad selected by hardware ID, host stop and reply order"
author: Jack
date: 2026-11-23
branch: feat/native-firmware-versions
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/firmware_versions.rs
  - tools/openblade-windows-host/src/windows/scm/installed/firmware_versions_windows.rs
  - tools/openblade-windows-host/src/discovery/windows.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
---

Supersedes "Native firmware versions: managed record, McuVersion left Unavailable" (2026-11-23) on how the touchpad interface is found, the Host lifetime and the reply ordering; the rest of that learning still holds.

## Decided
- The RZ09-0581 touchpad is HID over I2C (`ACPI\PIXA3854`, service hidi2c), so its interface path is `\\?\HID#PIXA3854&Col02#...` and never contains `vid_093a&pid_3001`. The first version prefiltered paths on that text and opened nothing, so the touchpad was always Unavailable on the exact model the code serves (0 of 53 HID interfaces matched). It passed every test because the fakes listed attribute rows, never paths.
- The touchpad interface is now selected by its devnode hardware ID `HID\VID_093A&UP:000D_U:0005`, which hidclass builds for every top-level collection on every bus (the Col02 IDs read on this laptop are `HID\VEN_PIXA&DEV_3854&Col02 | HID\PIXA3854&Col02 | HID\*PIXA3854&Col02 | HID\VID_093A&UP:000D_U:0005 | HID_DEVICE_UP:000D_U:0005 | HID_DEVICE`). Only interfaces with that ID are opened (access 0, share read and write), and the VID, PID, usage and BCD checks stay on the attributes. The selection is a pure function, tested with the real I2C row, a USB row, the sibling mouse collection and a 1532:02E0 control collection.
- Managed's open-everything approach is deliberately not copied: it would open the laptop's own control collections. The result is the same for the touchpad.
- `pnp::facts` cannot be used for this: a vendor-only `HID\VID_xxxx&UP:` alias without a PID makes it reject the whole ID list. The selection reads the ID list directly.
- Interface enumeration is discovery's (`discovery::windows::hid_interfaces`, over `WindowsMetadata::scan_hid`), and the model catalog is discovery's `Inventory` (`discovery::catalog_model`, which errors on an invalid inventory instead of defaulting to an empty list). Only the USB4 `DeviceInfoSet` remains in the firmware Windows module, since discovery's set enumerates interfaces, not devnodes.
- The Host holds the capture only until the first poll hands it to the worker, so the worker's drop is the last drop; tests use that as a real end-of-worker signal. `Host::stop` (called from `ReadSource::stop`) collects what was reported, ends listening and never starts an idle capture; a running worker ends after the read it is in.
- `observe_at` applies the firmware member after `decorate`, copied from the published active status, so a reply at revision N carries exactly what publication N carries.
- A Service whose reader did not start (`MailboxSource::NoReader`) publishes neither `firmware` nor `batteryHealth`, where managed captures both at construction. This is documented, not changed; moving the Host would be its own slice.

## Tried and ruled out
- A path prefilter of any kind for the touchpad: the path is bus-specific and says nothing about the vendor on I2C.
- Waiting on `Arc::strong_count` or a spin loop to know the worker ended: replaced by the Probe drop signal.

## Watch out
- A fake that lists attributes cannot catch a wrong enumeration or selection. Keep the raw-HID tests (`only_the_touchpad_interface_is_opened`) when touching the selection.
- Mutants for the two stop gates in `capture` (after the first and the Thunderbolt report) and for `Host::stop` are killed by dedicated tests; removing either gate or the stop call fails them.
