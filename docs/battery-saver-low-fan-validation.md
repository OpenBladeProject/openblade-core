# Battery Saver 1,500 RPM validation

This developer-only experiment checks whether the exact RZ09-0581 accepts and
physically reaches a fixed 1,500 RPM target in Battery Saver. The installed app
continues to admit 2,000–5,400 RPM. A successful experiment would apply only to
the device, firmware, power source and command sequence exercised.

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
contract. The named operation exposes no target-value parameter.

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

## Results and model scope

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
