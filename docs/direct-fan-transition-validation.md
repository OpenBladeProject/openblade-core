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

## Additional validation on 2026-10-14

The installed service remained
`0.26.1+86e0d91f964298a6f7a4051997838433d56f7d26` throughout these checks.
The new developer scenarios were built and tested at
`4b972e01e58abb598dda3414b0f1473719a1e954`: 1666 Protocol tests and 481 Capture
tests passed, with a zero-warning Capture build and successful required hosted
checks. Independent review covered the write/readback path and operator recovery.

### AC and battery round trip: passed

An attended disconnect/reconnect using the same laptop AC charger produced:

| Observed power source | Confirmed performance | Confirmed fans |
| --- | --- | --- |
| AC before disconnect | Balanced | Automatic |
| Battery | Battery Saver | Automatic |
| AC after reconnect | Balanced | Automatic |

Original profiles and overrides were unchanged. No competing controller or
unconfirmed write was present. Source inspection confirms that AC, battery and
USB-C events share the transition handler; USB-C was not physically exercised.
This check began with Automatic fans and does not validate a HyperBoost exit
or a manual-fan power-source transition.

### Service-owned manual preparation: failed, original state restored

A first preparation was rejected by temperature admission before any setting
change or process shutdown. After a cool baseline was confirmed, one typed
service request attempted a persistent Fixed/5400 override to establish owned
manual fans for the shutdown check.

The installed service reported an indeterminate `NativeIoFailure` after manual
entry channel 2 of 2. Its target-only continuation then stopped on a failed fresh
pre-write read before submitting any fan target. The operator stopped before
service shutdown and before any new developer scenario ran.

Two subsequent read-only observations confirmed generic manual state, with
both fans at 3800 RPM, no HyperBoost, exact-device admission, full AC, no competing
controller, and no unconfirmed write. The saved Fixed/5400 intent remained, but
5400 RPM was not established. Only a Fans drift advisory was present; generic
manual readback was not treated as exact target confirmation or ownership.

After that reconciliation, one explicit typed request cleared only the test fan
overrides to their original null values. The service returned Applied with
confirmed readback. A fresh observation matched the original Balanced/Automatic
state, profiles and all saved overrides. The 5400 request was not replayed.
All six protected installed-component hashes were unchanged; the service,
exactly one tray and one Session Agent remained running.

The private diagnostic archive preserves the failure message. It does not
include the underlying native Win32 error or I/O phase, so the cause remains
unresolved; the result must not be attributed to firmware or a competing app
without further evidence. No raw archive or private configuration is included
in this record.

### Remaining checks

The new target-only 5400/4000/5400, manual-to-Custom, manual-to-HyperBoost and
70%-to-protection-Off scenarios have passed source tests but have not run on
hardware. Service shutdown handback and saved-intent restart validation also
remain unperformed because preparation failed. Additional hardware writes
stopped after the reconciled original-state restoration.

Sleep/resume was not initiated. It remains the final attended step and requires
fresh approval immediately beforehand. Pad-removal behavior requires an actual
pad and owned pad-required HyperBoost state; this session does not establish it.
The production gates remain unchanged, and this evidence does not mark their
removal production-ready.
