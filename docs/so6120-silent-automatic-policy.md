# SO6120 Silent Automatic stopping conditions

The 2026-11-05 parallel follow-up resolves the factory Silent table's main
stopping conditions. Under steady-state Automatic control, a running stage-1
pair reaches the zero-target stage only after all seven enabled inputs are below
their lower bounds for the required
evaluations. A single input at its upper bound can request a rise. Low CPU/GPU
readings alone do not establish that these conditions hold.

This is offline analysis of the exact EC 1.04 image pinned in the
[original analysis](so6120-ec104-fan-analysis.md#input-and-method), checked against
NDS32 instructions and image bytes. The live active table, current input values
and physical response were not read. No hardware, updater, driver or installed
state was changed. The other parallel branch traces the
[legacy storage commands](so6120-host-mailbox-alternatives.md#legacy-timed-commands-are-storage-transactions).

## Seven-input hysteresis

The four identical 512-byte factory Silent tables use sensor mask `BF`, enabling
channels 0 through 5 and 7. Stage 0 requests 0/0 RPM; stage 1 requests
2,150/1,950 RPM. Loader `0x158FE` selects curve record `mode + 0x20`, so Silent
uses `0x25`. Checked persistent data can replace the factory table.

Comparator `0x1526C` reads inputs `0x8053C..0x80543` and bound pairs beginning
at `0x80520`. With the normal arguments `(0,1)`, its reduction at
`0x154EA..0x1550E` prioritizes UP when any enabled input is `>= upper`.
DOWN requires every enabled input to be `< lower`; otherwise it holds.
Equality therefore prevents a down transition and can trigger an up request.

| Channel | Stage 0: any input at or above requests UP | Stage 1: every input must be strictly below for DOWN |
|---|---:|---:|
| 0 | 60 | 50 |
| 1 | 56 | 50 |
| 2 | 53 | 46 |
| 3 | 53 | 50 |
| 4 | 52 | 46 |
| 5 | 38 | 35 |
| 7 | 35 | 33 |

These are processed byte values. They are not seven interchangeable CPU/GPU
Celsius limits. The inspected selector-0 and selector-2 factory tables also
have zero-target stages. Silent has at least as high stage-0 upper bounds on
every channel in that comparison, but the bounds on channels 5 and 7 are equal.

## Input provenance and the battery-temperature interpretation

| Channel | Firmware source | Exact bank-0 source alias |
|---|---|---|
| 0 | Filtered `0x80256`, copied to `0x8053C` at `0x139EC`; Silent uses a ten-sample history. | `10` |
| 1 | `0x80258`, copied to `0x8053D` by `0x10CCE`; bus address byte `9E`, command `00`. | `11` |
| 2 | `0x80322`, copied to `0x8053E` at `0x12C90`; bus address byte `90`, command `00`. | `30` |
| 3 | `0x80324`, copied to `0x8053F` at `0x12CA6`; bus address byte `92`, command `00`. | `32` |
| 4 | `0x80326`, copied to `0x80540` at `0x12CBC`; bus address byte `94`, command `00`. | `34` |
| 5 | `0x80328`, copied to `0x80541`; bus address byte `96`, command `00`. | No exact-address entry in the decoded map |
| 7 | Derived `0x8035E`, copied to `0x80543` at `0xFE98`. | `17` |

The four channel-2..5 transaction records are at `0x29BCC..0x29BEB` and
are consumed by `0x12BC4`. A source alias is not an atomic snapshot of the
comparator's copied inputs, and its writable map metadata grants no test
authority. Physical sensor locations and units for channels 0..5 remain
unassigned here. Failed reads and suppression flags can preserve cached values.

Channel 7 has stronger semantic evidence. Record 9 in the table at `0x299B0`,
located at `0x29A1C`, contains address byte `16`, command `08` and destination
`0x8035C`. Reader `0x10010` passes those fields to the bus helper. Neighboring
records match battery current, voltage, capacity and status commands.
[Smart Battery Data Specification 1.1, section 5.1.9](https://sbs-forum.org/specs/sbdat110.pdf)
defines command `08` as pack temperature in tenths of Kelvin. Firmware
`0xFE6E..0xFE98` clamps words below 2731 to zero; otherwise it stores the low
byte of integer `(word - 2731) / 10` in `0x8035E`, then publishes channel 7.

Together these establish a strong battery-temperature interpretation, inferred
from the exact data path and the standard. The factory conditions correspond
roughly to below 33 degrees Celsius for stage 1 to fall and 35 degrees Celsius
to rise from stage 0. Preserve the integer comparisons when analyzing captures;
this is not a calibrated physical measurement or a reading of this laptop.
Override bit 5 in `0x80425` skips conversion while still publishing the cached
derived byte. The new identification does not assign units to the other inputs.

The subsequent [Windows-only battery probe](windows-battery-observation.md#rz09-0581-observation-on-2026-11-05)
returned unsupported temperature on this laptop. It did not read the live EC
input or fan RPM, so these factory stopping conditions remain untested.

## Dwell, target decoding and PWM are separate

The five row-tail bytes decoded at `0x15222..0x15260` specify down dwell,
up dwell, PWM cadence after down, PWM cadence after up/stage zero, and stage-skip
flags. Silent's stage-0/1 values are `40,40,40,40,0`; all eight factory Silent
rows have zero skip flags. Normal transitions therefore choose adjacent stages.

The dwell branches compare `counter > limit` before incrementing. From an
initialized count of zero, limit 40 needs 41 increments followed by the
qualifying evaluation. HOLD resets the count; direction changes reset its
history. Selecting a new stage clears the decoded-row flag, so its targets are
loaded on a subsequent policy evaluation. This is an evaluation count, not a
40-second promise. Reload path `0x159B8` instead scans for an initial stage
without steady-state dwell; repeatedly writing modes can change the experiment.

Timer `0x667C` schedules the PWM supervisor every 50 base ticks and stage work
every 100, at different phases. Foreground dispatch `0x20FD4..0x2100C` consumes
pending work, which can coalesce; an additional event path can reevaluate policy.
The stage-zero PWM cadence of 40 gives a steady interval of 41 supervisor
activations. These relationships do not establish a maximum wall-clock delay.

Target decoding `0x14F40` raises targets below 3,000 RPM when either
`0x80B08` or `0x80C08` equals one. BRAM commands `E3/E2` set/clear the first
flag and `E5/E4` the second, through stores `0x2198/0x21AE`. The same flags
also suppress several input-refresh and peripheral paths. Their complete
external purpose is unresolved, and they are not fan-stop controls. The floor
runs when a row is decoded; the decoded-row cache also matters.

Ordinary tach/PWM controller `0x15510` adjusts the measured speed toward the
target on its separate cadence. A zero target with positive tach enters the
overspeed path, which can reduce duty and reach zero. With target and tach both
zero it writes zero at `0x1568C/0x15838`, but an already-satisfied stability
counter can restore a cached duty later in that call. Stage zero alone is
therefore not proof of immediate or sustained mechanical stopping.

A nonzero stage-1 target with zero measured speed reaches the ordinary restart
path when its duty/cap conditions hold. It raises duty to the configured
threshold if necessary and increments it (`0x155D2..0x15610` and
`0x1577E..0x157BC`). This is firmware-controlled demand and adjustment, with
physical spin-up still separate from the source-backed behavior.

## Consequence for the next comparison

The normal-policy route can be studied with existing admitted Automatic mode
and tach/status operations. A useful idle comparison keeps each selected mode
stable, records the starting fan state and both tach histories, and distinguishes
sustained zero from a single zero sample. CPU/GPU temperatures alone cannot
explain all seven gates; a source alias or generic-register collector does not
inherit permission to read the raw transport.

This investigation supplies the conditions to interpret such a comparison.
It does not resume the paused live comparison or establish an on-demand switch.
Direct-duty access, cooling recovery and exact restoration remain separate gaps.
