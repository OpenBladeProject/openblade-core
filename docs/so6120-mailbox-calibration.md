# SO6120 mailbox recorder calibration

The attended calibration on **2026-11-06, 01:09 Asia/Shanghai** completed a
five-second recording. Both sessions stopped cleanly, but offline inspection
is **inconclusive**: the held-file control passed and the transient file's
create/close pair had no name. No physical-action window ran.

This used the recorder and inspectors at commit
`bc50551bf6fabb865dd6c960980f2506bb3088a9`. The user was present and approved
continuing with calibration. One UAC launch ran only `Calibration`; its wrapper
locked and hash-checked the reviewed script and controller. It did not change
profiles or fan settings, open a driver device, evaluate AML or issue port I/O.

## Collection and offline controls

Both sessions returned native success for start, enable, query and stop, zero
event/buffer loss, and `4201` (absent) on the post-stop query. The recording
process exited. The shared interval lasted approximately 5.011 seconds; ETL
hashes and sizes matched the saved manifest and offline exports.

| Observation | Result |
| --- | --- |
| Object-provider events | 499,538 |
| Handle rundown records | 241,855 |
| File handles in rundown | 20,962: 20,068 named, 894 unnamed |
| File-create records | 12,071, all unnamed |
| File-close records | 12,404: 11,697 named, 707 unnamed |
| Held-file control | One exact named rundown match; passed |
| Transient-file control | Matching lifetime found, named-close requirement failed |
| ACPI method events | 66: 33 starts and 33 finishes |
| Named InpOut or target-method observations | Zero |

The transient control has one create/close pair with the recorded PID and
handle, the same File type and object, and timestamps inside its narrow
open/close envelope. Both names are blank. Earlier and later handle reuse
is outside that envelope. This establishes a correlated transient lifetime,
but does not satisfy the plan's named-close control. The report retains
`marker_control_failed`; no later name was attached across object reuse.

No `TBCC`, `ECRR`, `BMRD` or `BMWD` event appeared. The ordinary method events
validate normal decoding only; external/nested emitter coverage is still
unresolved. The failed name control and unnamed handles remain explicit
limits on the zero-target observation. This calibration is not a clean
physical-action challenge or a new ownership guarantee.

## Action-window capacity

Calibration-only returns `captured` before the recorder's action-volume gate.
That status therefore does not say a longer action batch fits its budget.

| Session | Live bytes at last query | Projected 50 seconds | Projected 60 seconds | Disk watermark |
| --- | ---: | ---: | ---: | ---: |
| Object | 15,335,424 | 153,354,240 | 184,025,088 | 67,108,864 |
| ACPI | 1,507,328 | 15,073,280 | 18,087,936 | 8,388,608 |

These are the existing gate's conservative linear projections, not measured
long-run rates. ETL bytes include buffer padding and startup costs. Both
projections exceed their watermarks even at the minimum 50-second window.
Final files were 39,780,352 and 1,638,400 bytes respectively, including stop
and rundown. Neither reached its hard cap.

Before a physical-action batch, qualify the transient named control and
revise the recording/export budget together. Increasing the ETL cap alone
would leave the exporter's event/byte limits unresolved. Preserve finite
windows, buffer/rundown headroom, zero-loss checks and exact session cleanup.
The separate bounded ACPI emitter review also remains unfinished.

## Provenance

- Manifest SHA-256: `2d923dcdc3e1280429b0b807c59cb5cdfbbaa55a32127f4a59e6d5a3e08eaaa2`
- Object ETL SHA-256: `05e705b03ba66ec747798b95c71c83196cf83b6b7b4d0fce248ec78047ffd867`
- ACPI ETL SHA-256: `4a3102c034ce2abefdadc337d742510feeabed83623e8610f70271e2f7fba5de`

Raw ETLs, exports and process/path identities remain in ignored local
artifacts. Independent read-only review verified the native stages, hashes,
volume gate, controls and raw transient pair. No second recording or
action-window test was performed in that first authorization.

## Separately authorized v2 calibration

At **2026-11-06, 01:48 Asia/Shanghai**, the user authorized one corrected
five-second calibration. Recorder v2 prepared and closed a one-byte ordinary
file before tracing, then reopened it read-only during the shared window.
A bounded same-handle path query and byte read succeeded before closing it.
The named ETW control still failed; no physical-action window followed.

Both sessions again reported successful start/enable/stop, zero loss and
post-stop absence. The recording process exited. The held marker passed.
The transient's unique File create/close pair lasted **0.8046 ms**, with the
same PID, handle, File type and object inside its narrow recorded lifetime.
The raw payload names are empty. The same PID/handle was reused 0.605 ms
after the marker closed, and its object pointer appeared in a separate later
lifetime. Borrowing either later name would falsely validate this control.

| Observation | v2 result |
| --- | --- |
| Object-provider events / handle rundown | 279,592 / 240,836 |
| File handles in rundown | 20,198 named, 888 unnamed |
| File-create records | 1,592, all unnamed |
| File-close records | 1,528 named, 124 unnamed |
| ACPI events | 68: 34 starts and 34 finishes |
| Named InpOut or target-method observations | Zero |
| Object live bytes / final bytes | 4,587,520 / 27,131,904 |
| ACPI live bytes / final bytes | 1,048,576 / 1,179,648 |

The v2 60-second live-byte projections are 55,050,240 and 12,582,912 bytes,
within its respective 192 MiB and 24 MiB watermarks. The revised capacity is
adequate for this calibration's projection, not a guarantee against bursts.
The offline tool still reports `marker_control_failed` and `inconclusive`.

This separates two observations: short-lived handle **detection worked**,
while name visibility for this controlled lifetime did not. The held-file
name and other named closes show that names can be decoded. The unknown-name
population remains an attribution limit; it is neither proof of a competitor
nor evidence that every potential InpOut client would be visible. Further
filesystem-path variations would not resolve that uncertainty. A target-device
handle positive control, if separately source-reviewed and authorized without
port commands, is a more relevant next measurement.

The [Event 7 instruction audit](so6120-acpi-event-coverage.md) separately
establishes conditional external-root and EC-query coverage, and the nested
and reentrant blind spots. No additional quiet capture is needed to infer
that static distinction.

v2 provenance (SHA-256):

- Recorder script: `e726f7a3047522152c5250e2b9724ed2c6871b7b121440764ecf011be4d9c12a`
- Controller source: `3893bb3a47a93fe320244813602f0795c12a36985be35e94393e7b086d70b894`
- Manifest: `9f661b453e17d45b27a8faf28c4c50dc9f46325076b2fb72f5bf51cee3ef7fb0`
- Object ETL: `54b5b9444e31a2d53b852fa6e137f06d4f748c378b3954de1550b4f88c4e11bd`
- ACPI ETL: `3932d5fe97f93a70b916040ddc8534880fdbfbfdfd57abd47b597bb6d3226e78`

Independent review checked the pair directly in the ETL as well as decoded
records, its reuse/duplicate history, and both clock anchors. No driver device
was opened, no port/AML command ran, and fan/profile settings were unchanged.
