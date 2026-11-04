# Battery Saver 1,500 RPM validation

The initial developer-only experiment checks whether the exact RZ09-0581
accepts and physically reaches a fixed 1,500 RPM target in Battery Saver. A
separately authorized follow-up checks whether reducing a confirmed spinning
2,000 RPM target to 1,500 RPM stops the fans. Ordinary fixed-target controls
continue to admit 2,000–5,400 RPM. Results apply to the device, firmware, power source and
command sequence exercised.

## Source and operation

The pinned Product 736 files in [Synapse adapter wattage evidence](synapse-adapter-wattage.md#pinned-vendor-files)
were retrieved as data and their complete SHA-256 hashes verified. In the base
device bundle, `getThermalFanSpeed` independently returns the stored target byte;
`setThermalFanSpeed` takes class, thermal ID and target byte. Product 736's
`getPerformanceFanSpeedProfile` defaults to class `1` and divides RPM by `100`.
The target register is distinct from the measured-RPM query.

| Operation | Class / command | Payload |
| --- | --- | --- |
| Read mode | `0D/82` | Existing exact-model thermal query for channels 1 and 2 |
| Select Battery Saver manual fans | `0D/02` | `01 <channel> 03 01` |
| Read stored target | `0D/81` | `01 <channel> 00` |
| Set 1,500 RPM | `0D/01` | `01 <channel> 0F` |
| Read measured RPM | `0D/88` | `01 <channel> 00` |
| Restore Battery Saver Automatic | `0D/02` | Exact saved paired mode payloads |

The setter's byte encoding establishes how to submit this experiment, rather
than proof that firmware accepts a target below the advertised range. Zero-RPM
target semantics remain unreviewed.

## Execution

Run only with explicit authorization for the out-of-range 1,500 RPM experiment.
The baseline must independently confirm Battery Saver / Automatic on battery,
and both stored targets must be restorable within the existing 2,000–5,400 RPM
contract. Each named operation exposes no target-value parameter.

`build/Invoke-BatterySaverLowFanValidation.ps1` saves installed state and hashes,
uses an elevated child for the sole-owner handoff, and invokes the built Capture
command once. The child waits for both SCM Stopped and the original service
process to exit. It leaves the installed executable files unchanged. After
confirmed restoration, the normal-integrity parent restores the original user
process presence and confirms saved profile and override intent.

The runner saves paired mode, both stored targets and initial measured RPM in a
durable JSONL journal before any test write. Every write submission rechecks exact
identity, recorded firmware, competing-controller absence, exclusive ownership,
active local console session and battery power. The battery must report at least
20% charge. Candidate admission requires a confirmed CPU temperature at most
55°C, with a reading no older than two seconds at native submission. Restoration
retains the other gates while allowing firmware control to resume if temperature
has risen.

The test enters Battery Saver manual fans directly, sends 1,500 RPM to both
channels, confirms mode and stored targets independently, then records up to 30
one-second tachometer samples. Three consecutive samples with both fans between
1,300 and 1,700 RPM establish the measured target tolerance. Clamped target or
tachometer results are reported as failures. With unambiguous readback, the
runner restores both original stored targets and the exact paired Automatic
mode, then independently verifies both.

Ambiguous submission or readback stops further writes, including restoration.
The handoff leaves installed owners stopped when restoration is unresolved.
No alternate sequence or uncertain write is replayed.

## Initial experiment: automatic baseline

Software validation: 2,691 Protocol tests and 505 Capture tests pass, including
33 focused runner tests. An independent source safety review passed.

The first owner handoff stopped before invoking Capture because an installed
owner remained alive after SCM reported Stopped. No 1,500 RPM request was
submitted. The service and original user processes resumed; a fresh typed status
read confirmed Battery Saver / Automatic and active exact-device admission. The
wrapper now waits for the exact original service process to exit.

The live experiment completed on 2026-11-04 with BIOS 4.01, battery power and the
Windows CPU temperature reading at 45°C throughout candidate admission. The
[sanitized result](evidence/battery-saver-1500-rz09-0581-2026-11-04.json)
retains all 30 tachometer samples and the exact baseline and restoration pairs.

| Observation | Result |
| --- | --- |
| Original state | Battery Saver / Automatic, both stored targets 3,800 RPM, both actual readings 0 RPM |
| Candidate mode | Both channels independently confirmed Battery Saver / Manual |
| Candidate stored targets | Both independently read back 1,500 RPM |
| Candidate actual RPM | Both remained 0 RPM in all 30 samples, spanning about 37 seconds |
| Restoration | Both stored targets returned to 3,800 RPM; exact paired Battery Saver / Automatic state confirmed |
| Installed state | Service running, original tray and Session Agent presence restored, profile and override intent unchanged, protected executable hashes unchanged |

The experiment reports `ActualRpmDidNotSettleAt1500`, with restoration confirmed
and no ambiguous submission. It confirms acceptance of the stored target, but
does not validate physical 1,500 RPM operation. Both fans were already reading
zero before the request, so it also cannot establish that 1,500 RPM forced them
to stop. Spin-down from a running baseline, restart behavior, load response and
Silent mode were not exercised.

Fixed-target limits differ between models and presets; matching command
geometry alone does not establish a lower physical fan limit. Normal controls
retain their existing ranges on this laptop and every other model.

## Follow-up: spinning 2,000 RPM target to 1,500 RPM

The initial zero-RPM baseline left spin-down causation unresolved. A separately
authorized follow-up therefore establishes both fans spinning before reducing
the target. This is an experiment starting condition, rather than a firmware
prerequisite for direct low-target entry.

The named `validate-battery-saver-spin-down-1500` command enters Battery Saver
manual fans, sends 2,000 RPM to both channels and independently confirms the
mode and stored targets. It requires three consecutive measured pairs with
both fans in the 1,800–2,200 RPM tolerance. A known preparation failure skips
1,500 RPM and restores the saved state after fresh admission; ambiguity stops
further writes. With preparation confirmed, it changes
only the two targets to 1,500 RPM, then requires three consecutive measured
pairs of exactly zero to report spin-down. The same submission admission,
durable journal, ambiguity stop and exact restoration rules apply. The owner
handoff wrapper selects this command with `-ConfirmSpinningAt2000`.

The follow-up completed on the same RZ09-0581, BIOS 4.01, on battery in Battery
Saver. The Windows CPU temperature reading remained 45°C during candidate
admission. The [sanitized follow-up result](evidence/battery-saver-2000-to-1500-rz09-0581-2026-11-04.json)
retains every preparation and candidate sample and all ten acknowledged writes.

| Observation | Result |
| --- | --- |
| Preparation target | Both independently read back 2,000 RPM |
| Confirmed spinning baseline | Three consecutive pairs: 1,800/1,800, 1,800/1,900 and 1,900/1,900 RPM |
| Reduced target | Both independently read back 1,500 RPM, with Battery Saver / Manual retained |
| Physical response | Both fans reached zero about 10.8 seconds after the second 1,500 RPM acknowledgement, followed by three consecutive 0/0 readings |
| Restoration | Both original 3,800 RPM targets and exact Battery Saver / Automatic paired state independently confirmed |
| Installed state | Original profiles and overrides, user-process presence and protected executable hashes verified after restoration |

This establishes observed spin-down from running fans after the target-only
change to 1,500 RPM in the sampled cool Battery Saver conditions. Steady
1,500 RPM rotation, persistent fan-off under load, restart behavior, Silent
mode and other models were not validated. Normal control ranges remain
unchanged. The updated probe passes 2,717 Protocol and 505 Capture tests,
including 59 focused runner cases, and independent safety review.

## Lowest nonzero target: 100 RPM

The target field uses 100 RPM units. A literal 1 RPM value cannot be represented;
wire value `01` requests 100 RPM. Zero's firmware semantics remain unknown and
zero was not tested.

The separately authorized `validate-battery-saver-spin-down-100` trial first
confirmed both fans spinning at the 2,000 RPM target. Both 100 RPM setters
returned acknowledgements, but the immediate paired mode readback unexpectedly
reported `01010000` / `01020000`. The probe stopped all further writes, including
restoration, and left the installed owners stopped for read-only reconciliation.

Fresh read-only reconciliation subsequently confirmed a stable Battery Saver /
Manual pair, both stored targets still at 2,000 RPM, and actual readings of
1,900/2,000 RPM. The 100 RPM request therefore was not retained at reconciliation;
the acknowledgements alone did not establish successful application or fan stop.
This does not establish whether wire value `01` has special firmware semantics.

A separately reviewed restoration-only path repeated the stable current-state
reads in a fresh session, then issued only the four original baseline commands:
3,800 RPM to each target and the exact Battery Saver / Automatic pair. Independent
readback confirmed restoration. The installed service and user components were
restarted, protected executable hashes were checked across recovery, and all ten
compared profile and override fields matched the saved baseline. No 100 RPM
command was replayed. The original failed trial remains recorded as failed;
the successful explicit recovery is a separate result.

The [sanitized 100 RPM and recovery record](evidence/battery-saver-100-rz09-0581-2026-11-04.json)
retains both outcomes. The verified 1,500 RPM path remains the basis for the
Battery Saver fan-stop feature.

## Battery Saver switch

The product adds a default-off **Stop fans** switch in the Fans card for exact
RZ09-0581 (`1532:02E0`). Its separate battery-profile preference preserves the
normal fan mode, fixed speed and curve, including current overrides. It applies
only on battery in Battery Saver. Turning it off or leaving that scope restores
the current resolved normal fan intent.

The service directly selects Battery Saver manual fans when needed and writes
the established 1,500 RPM target; the experiment's 2,000 RPM preparation is not
a product prerequisite. Paired mode and both stored targets must independently
confirm the request. Actual RPM remains a separate measurement: confirming the
target does not guarantee that firmware will hold the fans stopped.

Admission requires CPU temperature at or below 55°C, confirmed inactive GPU
activity and fresh sensor evidence. Unsafe or unavailable readings release
OpenBlade-owned fan stop to firmware Automatic and suspend the request for that
Battery Saver activation. The preference stays on; turning it off and on can
retry after conditions permit it. Native submissions retain fresh exact-device,
controller, physical-power and scope checks. Ambiguity pauses further writes.
Low-power and shutdown cleanup release only service-owned fan control and do
not change saved intent.

After a service restart, a matching observed target does not establish ownership.
A new successful, independently confirmed fan-stop write can establish it while
conditions permit. If the observed stop target is unowned and conditions prevent
that write, control is paused: turn the switch off to restore normal cooling.
The service does not silently adopt or automatically overwrite unowned state.

The new switch has software and disconnected-render validation. Its installed
service integration and sleep/resume behavior have not been physically tested.
The user-authorized hardware trials above exercised the developer tool, and the
original installed state was restored. Contracts/IPC changed, so deployment
requires a coordinated full install rather than a tray-only replacement.
Other models and Silent mode remain outside this feature's admission; shared
fan-command encoding alone does not justify a lower physical limit.
