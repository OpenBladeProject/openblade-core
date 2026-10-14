# Direct fan transition validation

On 2026-10-14, a typed, reversible test passed on a Razer Blade 16
RZ09-0581 (`1532:02E0`), BIOS 4.01, using the laptop's AC charger with
confirmed full-power AC admission. The result supports removing the separate
Balanced/Automatic preparation step for this command sequence.

This is physical validation of existing admitted commands, not a new vendor
capture oracle or a change to production admission. Tracking:
[issue #750](https://github.com/OpenBladeProject/openblade-core/issues/750),
[PR #751](https://github.com/OpenBladeProject/openblade-core/pull/751).

## Tested source and conditions

The validator was built from commit
`980342437abce615e5ce242f3d5194ee24659091`.

| Artifact | SHA-256 |
| --- | --- |
| OpenBlade.Capture.exe | `40010BA354AA6D0BCE1F11343F95535F2DF0EE0292853AB67BDA87062A8BE4BE` |
| OpenBlade.Protocol.dll | `8C1ED5120B1786E86E4F40758913EA7AD3EC20DB2FC15B55E7BB1319D152D87A` |

The original installed state was Balanced/Automatic with no performance or fan
override. A typed service operation prepared Performance/Automatic. The tray,
Session Agent and service then stopped, leaving the developer validator as the
only control-HID owner. Exact-device, competing-controller, ownership and
full-power AC admission were checked, including fresh admission before every
apply and restoration command. USBPcap was not running.

## Direct round trip

Two independent baseline reads matched Performance/Automatic. The following
setter payloads were submitted through the existing typed commands:

| Step | Selector 1 | Selector 2 |
| --- | --- | --- |
| Enter manual state | `01010001` | `01020001` |
| Request fixed 5400 RPM | `010136` | `010236` |
| Restore saved Performance/Automatic | `01010200` | `01020200` |

No Balanced/Automatic preparation setters (`01010000`, `01020000`) were sent
between the Performance/Automatic baseline and manual entry.

Apply was acknowledged, and independent paired readback confirmed manual state.
Across 15 RPM samples, the fans settled to 5300 and 5100 RPM. The last three
samples were within the validator's 5000-6000 RPM tolerance for both fans.
Restoration was acknowledged, and paired readback exactly matched the saved
Performance/Automatic baseline. The validator reported success, with no
indeterminate partial write or manual recovery requirement.

## Original state and installed components

After service restart, startup reconciliation had already reapplied the original
Balanced/Automatic profile. The operator wrapper's final restore precondition
therefore rejected an additional write and returned a failure status. A fresh
read-only reconciliation confirmed the original profile set and all seven
saved overrides were unchanged, with no external changes or unconfirmed writes.
The physical validator itself had completed successfully before restart.

The service was running, with exactly one installed tray and one Session Agent.
All six protected installed-component hashes matched their pre-test values.
No installed binaries were replaced. Final reconciliation completed at
`2026-10-14T09:51:02.4690258Z`.

## Scope

This result validates direct Performance/Automatic to fixed manual fans and
direct return to Performance/Automatic on the tested device and AC conditions.
Generic manual readback does not establish that Performance or Custom CPU/GPU
power settings remain effective while manual fans are active.

RZ09-0528, Custom power settings, HyperBoost, battery or USB-C transitions,
shutdown and sleep/resume were not physically tested. The installed production
Balanced gate remains unchanged; removing it is a separate product change.
