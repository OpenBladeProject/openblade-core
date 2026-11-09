# RZ09-0530 direct-duty source review

Reviewed 2026-11-08 against core `71193867c99dcabde57fd1a56322eae75d072f05`.
Exact PI4110 EC 1.03 and its packaged host code establish the two-fan register,
transport and Automatic handback contract needed by the guarded host-mailbox
backend for Blade 14 (2025), RZ09-0530 (`1532:02C5`). This is **source-reviewed;
physical stop and recovery are untested**. The ordinary HID backend's
Automatic-only behavior does not describe this separate EC interface.

The [offline profile](../tools/firmware/profiles/pi4110-ec-1.03.json) pins the
carrier/image, decodes 2,048 descriptors and annotates 13 control/observation
fields. It exposes no live control API. Other AMD models require their own
review. No vendor executable or assembly was run; no hardware, ACPI method,
driver installation or live setting was accessed. Full static artifacts remain
ignored under `artifacts/model-expansion/amd0530`.

## Exact sources and identity

The official [EC article](https://mysupport.razer.com/app/answers/detail/a_id/21228)
identifies this model and EC release. The freshly downloaded carrier's size and
hash matched the pinned model catalog before extraction; Authenticode was Valid
for Razer USA Ltd. Extraction checked the EC hash before disassembly.

| Input | Bytes | SHA-256 |
|---|---:|---|
| [PI4110-ECv0103.exe](https://dl.razerzone.com/drivers/Razer%20Blade%2014%E2%80%9D%20%282025%29%20%7C%20RZ09-0530/PI4110-ECv0103.exe) | 1,465,984 | `c260bd7596aef0f4a9630aecd47bbf6cc49accfd2a03eb0c9642643aac46b7f8` |
| `PI4110_EC_0103.bin` | 262,144 | `aecacca42102c17f4b2731a2ab28ebd92e154d0d997a708e41e7e36d56fc2224` |
| Packaged main updater, member 3 | 390,856 | `1bc6e62f0ca5be1be56a3b12ee82306fd61406287fde099d5369437fe7fa3616` |
| Packaged EC flasher, member 9 | 27,648 | `3de34bf3f5e57e0c11e76f45b773642ae3816f4a645040cf3b0602dcd30e79ed` |

The EC is member 7 at NSIS stored-length offset `0x0FEDF6`; the main updater
is member 3 at `0x02DFD1`. ToolConfig identifies image PI4110_EC_0103.bin,
SSID `2023` and version-address `9B`. Packaged release metadata identifies
RZ09-0530, SSID `1A582023`, EC `01.03`. Existing exact Synapse Product 709
pins establish `1532:02C5` and the reviewed `07/8C` adapter query in
[synapse-adapter-wattage.md](synapse-adapter-wattage.md). This review does not
introduce a new HID query or claim live firmware hash attestation.

The EC uses RV32 compressed instructions at `80000000`; subtract that base for
file offsets below. Startup `8000013C..80000162` copies 36 bytes from file
`239B6` to `80081000`. The first eight words point to the descriptor banks;
bank zero starts at file `22FA8`. Descriptors contain four-byte little-endian
metadata followed by a four-byte backing pointer. The profile checks these
bytes directly. Low RAM operands and high startup addresses are recorded as
encoded; no physical aliasing claim is required to reinterpret the descriptors.

## Host ingress, completion and readback

Static PEReader inspection of this carrier's main updater establishes:

- `RWIo.WriteRzEcMbox`, token `0600001F`, RVA `2167`, writes the index to
  port `300`, then the value to `301`. `ReadRzEcMbox`, token `06000020`,
  RVA `217F`, writes the index to `300`, then reads `301`.
- `RWIoI2C.I2C_Read_Write`, token `06000051`, RVA `2E90`, uses address `58`:
  bank `90`, index `91`, count `92`, payload `A0` onward, command `93`.
  Read publishes `A0` last at IL `0039..0043`, then polls command zero at
  `0048..0052`. Write stages payload before publishing `A1` at
  `01C4..01CE`, then polls zero at `01D3..01DD`.
- Final IL `02D2..02E6` accepts status `94==80`. Vendor polling alone does
  not establish ownership or provide the guarded backend's recovery behavior.

EC handler `80014E86` consumes `F02290..F02294` and `F022A0`. Its
`80015132..80015144` checks bank/count, `80015154` selects the bank pointer,
and `8001515A` writes status `80` **before copying**. Read loop
`8001517A..800151AA` and write loop `800151CE..80015218` wrap indices to
8 bits. Write skips metadata `1` at `800151FC`; permitted store is
`80015204`, followed by the mapped-change flag at `80015208`.
Bank/count/command errors are `84/82/83`.

Caller `8000EC04` calls the handler at `8000EC08`, then clears command
`F02293` at `8000EC10` after return. Therefore require command zero, success
status, consistent descriptor and a separately published fresh readback.
Status can report success for a skipped read-only field. Staged payload `A0`
is unrelated to bank-zero register `A0`.

## Register semantics and direct entry

| Bank-zero index | Backing location | Meaning / access |
|---|---|---|
| `06` | `84EE3` | Active bit 0; read-only |
| `08/09` | `80816/80817` | Measured speed, 100 RPM/count; read-only |
| `0A/0B` | `80940/80941` | Separate policy command/status; writable/read-only |
| `1A` | `8035F` | Normal `0..7`, direct `FE`, target `FF`; writable |
| `36` | `8034D` | Manual flags, bit 0 manual handling; writable |
| `B0/B1` | `F01807/F01803` | Actual PWM bytes; read-only |
| `B2` | `F01841` | Shared PWM cycle byte; read-only |
| `B8/B9` | `80394/80395` | Staged raw duty bytes; writable |
| `F0` | `80425` | Override flags, direct permission bit 6; writable |

These 13 descriptors were independently asserted against this image. Writable
controls have metadata `2`; actual PWM and tach observations have metadata `1`.

Policy `8000915E` requires active bit 0 at `80009162..80009168`.
Dispatch `8000918A..80009198` separates FE/FF from normal selectors.
FE checks F0.6 at `800096F2..800096FA`. Stores `8000971C/8000974E` copy
B8/B9 to actual PWM unchanged, including zero; equality merely skips an
already-equal store. Permission absent falls back to selector zero at
`800097AE`, which is not exact restoration of an arbitrary captured profile.
Normal feedback also stores zero at `80008292/800082FE`.

Cycle initialization `8000720C..80007214` writes `B7` (183); runtime must read
the live cycle and bound raw counts against it. These are not percentages.
Tach publication `80007580..8000759C` divides measured-speed accumulators by
100. Requested zero, actual PWM zero, coarse measured zero and mechanical stop
are separate observations. No thermal sensor in degrees is inferred from
these 13 fields; host thermal admission remains a separate runtime condition.

The generic FE branch also copies `8038E/8038F` to `F01808/F01809` at
`80009780/800097A8`. This image's factory seeder `8000781A` selects four
regions `3B000/3C000/3D000/3E000`, each with eight 512-byte records. All
32 records encode fan count two in header byte 0's low three bits. Loader
`80008A44..80008A64` publishes that count; stage decoder `80007A56` consumes
two RPM targets and zeros unused target words. The generic extra slots do not
establish extra physical fans. No additional slot control is proposed.
Thermal tables are omitted from the profile; no other model's curves or
manual-extension layout are imported.

F0 is a whole-byte mapped write. Bit 5 suppresses update paths at
`8000C0F2`, `8000FFC2`, `80013DC8` and `80014C28`; bits 3/4 affect charger
policy at `8000CA10/8000CA30`; bit 1 affects state/input behavior at
`8000D6B4/8000D770`; bit 0 selects another PWM path at `8000EA66`.
Preserve unowned bits. Require a captured normal selector, manual flags zero,
policy command idle, bits 5/6 initially clear, active state, a valid live cycle,
and bounded requested/actual duty. Stage B8/B9, set only permission bit 6,
then select FE last.

## Automatic restoration and admission boundary

Supervisor `80008946` checks active state and skips normal feedback
`8000822A` only for selector FE at `8000898C..8000899C`. Permission bit 6
does not gate this normal feedback branch. Restoring the captured normal
selector therefore leaves FE before clearing permission. Independently observe
normal selector/manual zero/policy idle, restore the owned override byte, then
restore inactive staging bytes. Reobserve all owned controls; do not infer
settled fan speed from selector readback.

FE retains last-normal selector `8034F` while clearing stage bookkeeping.
Normal reload depends on prior selector, refresh/manual state and `8044E` at
`800091E4`; `80009250` can clear manual flags and `800092AE` records the
last normal selector. Policy status 4 at `8000FA58` follows an evaluator call;
it is not proof of Automatic or a cooling guarantee.

The mailbox is shared: no universal lock, firmware lease or crash-proof Auto
handback was established. Runtime must retain the existing process ownership,
Synapse exclusion, fresh identity/power/thermal gates, bounded transactions,
independent readback, journal, watchdog and guardian recovery. Stop eligibility
is exact RZ09-0530 plus reviewed EC 1.03, not every PI4110 image or AMD model.
Source identity/version checks do not cryptographically attest the running EC.

The independently verified delta from SO6110 is image identity and instruction/
table locations; the 13 register meanings, host transaction and selector-first
handback match. No new transport command or two-fan coordinator behavior is
required. Physical stop, restart, contention and recovery remain untested.

Reproducibility: static listing `ec-disassembly.txt` SHA-256
`19b4387326a139a8b94e74e4fb75457d79e2c1334d3e99ad37999b3d7673bb0c`;
main updater metadata/IL `host/block-03-metadata-il.txt` SHA-256
`96354b61b684cdf8bba55affb6022f716a26342581d88aa0468f8d758a003ff3`.
The exact carrier passed the offline inspector: 2,048 register descriptors,
13 annotated fields, no thermal-table import and zero warnings.
