# SO6120 EC 1.04 fan-control analysis

Static analysis on 2026-11-05 found a concrete reason for the different low-RPM
behavior of Silent and Battery Saver. In the recovered normal manual-control
path, requests below the table minimum become zero only for selector `3`.
Other normal selectors, including Silent `5`, clamp them to the minimum.
The image's four Silent factory tables specify a 2,000 RPM manual minimum.

Silent Automatic has a separate zero-target stage. It depends on multiple
inputs, timing, state and overrides. An internal direct-duty path also exists.
The [MCU 1.07 follow-up](so6120-mcu107-ec-fan-bridge.md) establishes a generic
register bridge through `1532:02E0`, but no supported, thermally supervised
fan-off operation. This analysis changes no production capability or hardware
admission.

## Input and method

The user supplied `SO6120_ECv01.04.exe`. The carrier was inspected and extracted
as data; it was never executed. No hardware access, firmware modification,
driver loading or installation was performed for this analysis.

| Artifact | Identity |
|---|---|
| Signed updater | 652,560 bytes; Windows Authenticode Valid, Razer USA Ltd. |
| Updater SHA-256 | `E6E105CA059B96C7DD0AE0BC2FBC0E259B29250B97F09447044624F2447CDAE7` |
| Extracted EC image | `SO6120_EC_0104.bin`, 262,144 bytes |
| Image SHA-256 | `8537E9FD55B7B35504D8D87B0191ACC4391B67365B3FE72F6E1029A424C9E707` |
| Package configuration | `SKU=RZ09-0581`, `ECVER=01.04` |
| Image identification | `ITE8320EC-V01.04`, NDS32 GCC toolchain |

Ghidra 12.1.3 decoded NDS32 little-endian data with big-endian instructions.
The initial vector branches to coherent startup at `0x5000`. Key conclusions
were checked against instructions and image bytes, independently of the
decompiler. Original EC source was not included: the C-like output is recovered
pseudocode, and the packaged PDBs describe the Windows updater.

All addresses below refer to this exact extracted image or its decoded RAM/MMIO
addresses. They are evidence locations, not a register-write recipe. Full local
analysis remains in ignored `artifacts/silent-firmware-research`; proprietary
payloads and full decompilation are not included in this repository.

## Manual requests and the selector-3 exception

The already pinned [Product 736 definitions](silent-low-fan-validation.md#reviewed-source)
map Silent to selector `5` and Battery Saver to `3`. These names are external
protocol evidence; the EC image does not supply those human-readable labels.

Normal-mode routine `0x15E46` calls `0x15C62` at `0x15EFA` for manual control
when `RAM[0x8034D]` bit 0 is set and `RAM[0x804D0]` bit 0 is clear. Per-channel
gates must also be zero (`0xF0162E`, `0xF01658`, `0xF0162F`, `0xF0165A`). Within
that path, the recovered target selection is:

```text
if requested_100rpm > table_maximum:
    selected = table_maximum
else if requested_100rpm < table_minimum:
    selected = 0 if mode == 3 else table_minimum
else:
    selected = requested_100rpm
internal_target_rpm = selected * 100
```

For the first fan, `0x15C88..0x15C9C` performs the minimum comparison and the
selector-3 exception; `0x15C9E..0x15CAE` multiplies by 100 and stores the internal
target. Equivalent branches exist for the other channels. This maps **any**
below-minimum value to zero in selector 3; it is not a special interpretation
of wire byte zero alone.

The active table's extension offset is encoded in bytes 3/4. Instructions
`0x16406..0x16418` load extension field `+0x21` into the minimum at `0x80555`;
`0x16432..0x16444` load field `+0x26` into the maximum at `0x8055A`.
The four factory selector-5 tables at `0x3BA00`, `0x3CA00`, `0x3DA00` and
`0x3EA00` have extension offset `0x1A8`, minimum byte 20 and maximum byte 54:
2,000–5,400 RPM in this calculation. All four 512-byte tables share SHA-256
`ECE5E29031EDBF6B4347F1413B55CEAB510CC629E3C9AB6415A576200B244AF7`.

This explains the previously recorded [Silent manual trials](silent-low-fan-validation.md#results):
stored targets of zero and 1,500 RPM coexisted with measured 1,900–2,000 RPM.
It also provides an independent mechanism consistent with Battery Saver's
earlier spin-down. The earlier trial still lacks a sustained control and
reversal; this static finding does not replace physical validation of reliable
on-demand stopping, service integration, restoration or lifecycle behavior.

## Automatic stopping is a separate path

Loader `0x158FE` selects table slot `mode + 0x20`. Default seeding at `0x14D28`
selects one of four board-configuration groups at `0x3B000`, `0x3C000`,
`0x3D000` and `0x3E000`. The eight-stage selector-5 table enables channels
0–5 and 7, with two target bytes and seven pairs of input bounds per stage.

Routine `0x14F40` multiplies automatic target bytes by **50**, independently
of the manual request's 100-RPM units. Stage 0 targets 0/0 RPM; stage 1 targets
2,150/1,950 RPM. Routine `0x1526C` evaluates the enabled channels against paired
bounds, with timing and stage transitions elsewhere in `0x15E46`.

| Raw channel | Stage 0 upper bound | Stage 1 lower bound |
|---|---:|---:|
| 0 | 60 | 50 |
| 1 | 56 | 50 |
| 2 | 53 | 46 |
| 3 | 53 | 50 |
| 4 | 52 | 46 |
| 5 | 38 | 35 |
| 7 | 35 | 33 |

These are raw input bounds, not identified CPU/GPU Celsius thresholds. Sensor
mapping, filtering, dwell counters and state remain relevant. In particular,
`0x14F40` raises decoded targets below 3,000 RPM to 3,000 when either
`RAM[0x80B08]` or `RAM[0x80C08]` equals 1; the meaning of these flags remains
unresolved. A zero table entry therefore does not guarantee a zero active
target. Routine `0x15510` contains explicit zero writes to the two duty
registers `0xF01807` and `0xF01803` in low/zero-target handling.

The firmware can load checked tables from persistent storage. This investigation
did not read the running EC's active table, so the factory bytes are not a live
table dump. Earlier Silent/Automatic zero tach readings remain separate
physical evidence of idle stopping.

## Internal duty control and the missing host contract

Selector `0xFE` in `0x15E46` reaches a separate path at `0x16262`. When
`RAM[0x80425]` bit 6 is set, it copies duty values from `0x80394/0x80395` to
`0xF01807/0xF01803`; otherwise it returns to the selected normal policy.
Routine `0x1586A` suppresses the ordinary adjustment loop for selector `0xFE`.
The [protection and restoration follow-up](so6120-direct-duty-safety.md) finds
that some thermal responses survive, but no bounded independent cooling
handback is established. The gating bit also changes a non-fan policy
calculation; it is not solely a fan-access permission.

The image includes a 90-byte USB feature parser and a class-`0x0D` dispatcher
whose command 2 assigns the mode. However, its USB descriptor at `0x2EC44`
identifies **1532:F003**, rather than the admitted laptop controller
**1532:02E0**. The subsequent [MCU analysis](so6120-mcu107-ec-fan-bridge.md)
traced non-USB EC interfaces and the normal controller's generic register
bridge. A bounded fan-duty command with known protection, ownership and
restoration semantics remains unestablished. Generic access and register
readback do not establish that contract.

Consequently, neither `0xFE`/`0xFF` nor raw RAM writes are proposed as tests or
product controls. This finding is scoped to the supplied RZ09-0581 image;
other models need their own firmware or demonstrably shared vendor evidence.
The paused Silent/Automatic idle comparison is independent of this static work.

## Other decoded data and review

Four embedded DTT vaults were decompressed; their compressed hashes matched
their headers, and all decoded payloads were identical. The decoded SHA-256 is
`D6274083678140FBE8E342782392D1B8C076656ED685F33A8E5A0DB44E4F2C7D`.
They contain power/application policy data; no fan-named key was found. This
bounded search supplies no separate fan-off contract.

Independent review checked the image hash, clamp instructions and caller,
table offsets/bytes/hashes, automatic override and USB descriptor. It confirmed
the static explanation while leaving a deployable direct-duty capability
unestablished. No production code, settings or installed files changed.
