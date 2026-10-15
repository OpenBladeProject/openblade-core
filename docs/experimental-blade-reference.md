# Experimental Blade implementation reference

This reference records the experimental backend's exact model identities,
command limits, recovery behavior, and evidence. For setup and test reports, use
[Experimental Blade support](experimental-blade-support.md).

## Device profiles

| Laptop | Exact Windows product name | USB identity | Admitted controls |
| --- | --- | --- | --- |
| Blade 16 (2023), RZ09-0483 | `Blade 16 - RZ09-0483` | `1532:029F` | Balanced, Silent, Performance, Custom, fixed fan targets, Maximum Fans, keyboard and logo lighting, 50-100% charge limits |
| Blade 14 (2023), RZ09-0482 | `Blade 14 - RZ09-0482` | `1532:029D` | Balanced, Silent, Custom, fixed fan targets, keyboard and logo lighting, 50-100% charge limits |
| Blade 14 (2025), RZ09-0530 | `Blade 14 - RZ09-0530` | `1532:02C5` | Balanced, Silent, Battery Saver, Battery Balanced, Custom, keyboard and logo lighting, 50-100% charge limits |
| Blade 15 Base (Early 2020), RZ09-0328 | `Blade 15 Base Model (Early 2020) - RZ09-0328` | `1532:0255` | Balanced, Silent, Gaming, Custom, single-zone keyboard lighting |
| Blade 18 (2023), RZ09-0484 | `Blade 18 - RZ09-0484` | `1532:02A0` | Balanced, Silent, Performance, Custom, Maximum Fans, keyboard and logo lighting, 50-100% charge limits |

Admission checks Razer and the exact product name in both Windows registry
sources, then the exact USB identity and 91-byte HID feature-report geometry.
Independent queries must identify one responding interface. SKU and firmware
versions are diagnostic metadata. Unknown models and ambiguous interfaces stay
blocked. Exact profiles enable their supported controls automatically.

For experimental models, Plugged In covers externally powered operation. The
backend does not distinguish barrel from USB-C or infer adapter wattage. The
Blade 14 (2025) admits Battery Saver and Battery Balanced only after fresh
battery-power checks at the initial gate and immediately before native
submission. AC presets remain external-power-only.

## Thermal controls

The thermal backend uses independent query and setter exchanges:

- preset and fan mode: `0D/82 [0, cluster, 0, 0]` and
  `0D/02 [1, cluster, preset, fan flag]` for clusters 1 and 2;
- Custom level: `0D/87 [0, cluster, 0]` and
  `0D/07 [1, cluster, level]`;
- fixed fan target: `0D/81 [0, zone, 0]` and
  `0D/01 [1, zone, RPM/100]`;
- Maximum Fans: `07/8F [0]` and `07/0F [2]` to enable or `07/0F [0]` to
  disable.

`0D/81` reports the stored fan target, not measured tachometer speed. Fixed
targets are 2,000-5,000 RPM on the Blade 16 (2023) and 2,200-5,000 RPM on the
Blade 14 (2023), in 100 RPM steps. Both zones must report the same admitted
target. Fan curves remain unavailable because the backend has no admitted live
temperature source.

Maximum Fans is a separate Custom-mode switch on the Blade 16 (2023) and Blade
18 (2023). A change reads `07/8F`, sends one `07/0F` setter, and requires an
independent `07/8F` result. It is unavailable in ordinary presets and is not a
substitute for adjustable fixed targets.
Select Automatic before enabling it; a Fixed baseline is rejected before any
setter is submitted.

All five profiles admit Custom CPU/GPU levels Low, Medium, and High. The Blade
16 (2023) also admits CPU Boost and the High/High combination. Other profiles
exclude High/High. Unrecognized raw levels remain diagnostic observations and
cannot become a guessed baseline.

Transitions go directly from the confirmed starting state to the requested
state. The backend does not reset through Balanced or Automatic. Preset writes
select firmware Automatic fans as part of the model command. A fixed-target
change preserves the confirmed ordinary preset or Custom mode and changes only
the fan flag and targets. Custom level edits on the two fixed-target models
preserve their confirmed fan flag and stored targets. Their explicit
`ReportsPerformanceWithManualFans` capability keeps this readable state separate
from backends whose manual selector hides performance. If a model cannot read and restore its current fan
state, the affected transition is blocked. Each submitted setting requires its
own exact readback; a mismatch or ambiguous submission pauses experimental
writes and never triggers an alternate sequence or setter replay.

The Blade 16 (2023) and Blade 18 (2023) can report firmware HyperBoost, but
OpenBlade exposes it as observed-only. It never submits a HyperBoost command.
Choosing Balanced, Silent, or Performance may replace an existing HyperBoost
state through that preset's ordinary direct transition.

## Battery protection

The Blade 16 (2023), both Blade 14 profiles, and Blade 18 (2023) admit every
whole charge limit from 50% through 100%. Limits from 50% through 99% enable
protection; 100% writes the firmware's protection-off value. The Blade 15 is
excluded because upstream reports that its firmware echoes the battery command
without applying it. Temporary full charge is unavailable.

Battery protection uses `07/92 [0]` for GET and `07/12 [encoded limit]` for SET.
Each operation first decodes and saves the raw protection flag and threshold,
performs a fresh native admission check, sends at most one setter, and reads the
result independently. A disabled baseline retains its raw threshold and flags
without treating them as an enabled limit. An enabled threshold must be in the
exact model's admitted range.

Blade 16 firmware may return the previous valid value for one to two seconds
after SET. Only that model uses the bounded confirmation allowance documented by
the pinned owner report: up to nine GET attempts, 250 ms apart. The setter is
never repeated. Invalid GET data, an I/O failure, cancellation after possible
submission, or a final mismatch pauses writes. The other three battery profiles
use one independent GET because no equivalent lag is documented.

The complete range on `029F` is directly owner-tested. The same range on
`029D`, `02C5`, and `02A0` is explicitly classified as source-backed
extrapolation from the shared encoding and each model's upstream 80%/off support.

## Lighting controls

| USB PID | Keyboard contract | Logo contract | Evidence boundary |
| --- | --- | --- | --- |
| `029F` | Brightness; Off, Spectrum, random Breathing, Rightward Wave; uniform Static, Ambient Awareness, Audio Meter | Off, Static, Breathing | Exact-model built-ins and 6x16 frame rows are owner-tested; frame color has no getter |
| `029D` | Brightness; Off, Spectrum, random Breathing, Rightward Wave | Off, Static, Breathing | Keyboard and logo on/off are owner-reported; richer logo modes are explicit extrapolation |
| `02C5` | Brightness; Off, Spectrum, random Breathing, Rightward Wave | Off, Static, Breathing | Keyboard and logo on/off are owner-reported; richer logo modes are explicit extrapolation |
| `0255` | Brightness; Off, Spectrum, random or single-color Breathing; single-zone Static | None | Single-zone color and its RGB getter are exact-model tested; Wave is excluded |
| `02A0` | Brightness; Off, Spectrum, random Breathing, Rightward Wave; uniform Static | Off, Static, Breathing | Uniform Static is extrapolated from the same command family and has no color getter |

Built-in keyboard effects use `0F/02` with independent `0F/82` readback.
Brightness uses `03/03` and `03/83`. Logo state and effect use `03/00`,
`03/02`, `03/80`, and `03/82`. Unknown effects, unsupported parameters, and a
non-Off effect whose raw brightness presents as 0% block mutation. Returning to
the same displayed brightness does not prove the exact raw 0-255 register was
restored, and an Off logo can hide its stored effect; diagnostics retain the raw
baseline needed for validation.

Blade 15 Static is one RGB zone. It writes `03/0A [06, R, G, B]` and requires
the independent `03/8A [00, 00, 00, 00, 00]` response
`[06, R, G, B, 00]`, together with the built-in effect readback.
Single-color Breathing uses `03/0A [03, 01, R, G, B]` and independently reads
back its mode and RGB through `03/8A`. Random Breathing requires the known
all-zero standard-effect response; other unrecognized payloads are rejected.

Blade 16 and Blade 18 Static use a uniform frame: six `03/0B` rows with
`[FF, row, 00, 0F, RGB x 16]`, followed by `03/0A [05, 00]` activation. These
exchanges confirm only successful command envelopes. Firmware exposes no RGB
frame getter, so OpenBlade records owned intent as applied without color
readback. It saves a readable built-in effect and brightness baseline before
the frame, and restores and confirms that baseline through the built-in getters
when frame ownership ends. A failure after any row may have partially submitted
the frame and therefore pauses writes.

The uniform frame is one color across the keyboard. It does not admit a key
layout, per-key or spatial effects, or the `02E0` shared ambient-frame API.
Blade 18 stays Static-only for this frame path. Blade 16 uses a separate
typed uniform-frame path for Ambient Awareness and Audio Meter. The Session
Agent reduces a sampled frame to one RGB color and sends it through the service;
this still has no RGB readback.

## Recovery

Choose **Resume experimental controls** in the experimental-support notice.
OpenBlade drains the previous session, checks the exact laptop and safety state
again, and reads the current supported settings without writing them. When those
checks succeed, choose the setting again. The uncertain request is not replayed.
A new lighting choice or an active profile edit that changes effective lighting
releases its hold. Failed reads or checks keep controls paused.

Install the complete coordinated pre-release. A tray-only replacement cannot
update shared contracts or the service backend.

## Diagnostic evidence

Exports read cached data without probing or writing the laptop again. Reports
stay local until the user shares them. There is no background upload.

`experimental-support.json` records the exact model, pinned sources, per-feature
evidence classification, write-pause state, and the last 32 typed observations.
Observations cover queries, raw baselines, submissions, independent readbacks,
and failures, with requested and confirmed values when available. Missing
readings stay unavailable. History is bounded and kept in memory; it is not a
complete audit or continuous telemetry recording. Serial numbers and raw HID
reports are excluded, and sensitive identity fields use the existing diagnostic
redaction.

## Source provenance

- [razer-ctl at `8e12e778`](https://github.com/tdakhran/razer-ctl/tree/8e12e778d303d4ed01eeb2c1e99ce2e97f1c8917)
  supplies the model/PID table, Blade 16 thermal capture table, and Blade 14
  contributor report.
- [maintained razer-ctl fork at `9c66e705`](https://github.com/sqmagellan/razer-ctl/tree/9c66e7055a0e31f5ec25c16185e66fde1f50cf4a)
  supplies the exact Blade 18 profile, Maximum Fans commands, Blade 16 frame and
  complete 50-100% battery tests, and the documented delayed GET behavior. Its
  [owner report #1](https://github.com/sqmagellan/razer-ctl/issues/1) confirms
  one RZ09-0484 unit without raw OpenBlade captures.
- [blade-control at `bc48c916`](https://github.com/jdsgnrinfo/blade-control/tree/bc48c916d4d6cc47df8c0e32a73ef454dda37cdd)
  remains the original model and battery-exception pin used by the experimental
  profiles.
- [blade-control at `92beedc8`](https://github.com/jdsgnrinfo/blade-control/tree/92beedc82e6e378cb71dac276e5820c8ea84247d)
  is the later feature pin. It supplies owner-tested Blade 15 single-zone RGB,
  Blade 15 Gaming/Custom, and Blade 14 (2025) battery modes. Its
  [owner report #2](https://github.com/jdsgnrinfo/blade-control/issues/2) records
  the RZ09-0530 behavior.
- [OpenRazer at `477f4d6d`](https://github.com/openrazer/openrazer/tree/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d)
  supplies exact lighting class/dispatch definitions. Model reports are retained
  in [PR #2237](https://github.com/openrazer/openrazer/pull/2237) for `029D` and
  [PR #2539](https://github.com/openrazer/openrazer/pull/2539) for `02C5`.

These projects share protocol ancestry but do not carry the same evidence for
every model. OpenBlade records direct owner testing, owner reports,
source-backed extrapolation, and SET-only behavior separately. Experimental
support remains distinct from OpenBlade's physically validated command catalog.
Per-model testing still needs to confirm application, readback where available,
restoration, and power and lifecycle behavior.
