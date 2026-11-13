# Blade 16 RZ09-0528 regional lighting

The exact SO6110 MCU 1.09 image independently establishes six regional 6x17
keyboard descriptor maps for `1532:02C6`. Their content matches the separately
reviewed SO6120 maps, but their selection code, transport and activation have
been reviewed from SO6110 itself. The former ANSI-only mask suppresses regional
Enter at slot 48 and the key beside it at slot 64. This is source evidence of
the shared software defect, not physical validation on a regional 0528 keyboard.

## Pinned source

| Artifact | SHA-256 |
| --- | --- |
| `SO6110-MCUv01.09.00.exe`, 1,442,040 bytes | `4d62ce4ae2c0faaa48f69e8a18816140ca23f1c9cb1cbc0d18a07a716f4025bc` |
| `SO6110_keyboard_FW_v1.09.00_20250918.hex`, 166,654 bytes | `234388a76438541b34907e46b95026ac7e91893640d369d98e69b9c7aaead7d9` |
| Decoded image, 59,420 bytes, base zero | `371c590b86e578720a063bd2e1ee6795a233dfb54dc09613472e4cfbe4d648c1` |

The [existing SO6110 source review](fan-stop-so6110-source-review.md) pins the
exact-model signed carrier. Its official source is
[Razer's 0528 MCU package](https://dl.razerzone.com/drivers/RazerBlade16RZ09-0528/SO6110-MCUv01.09.00.exe).
NSIS member 10 was decoded as data; the carrier was never executed. USB device
descriptor `0xDBD8` independently identifies `1532:02C6`. PI490 is a different
model's firmware and supplies no evidence for this implementation.

The [embedded report](../src/OpenBlade.Contracts/Fixtures/Blade16AmdKeyboardLightingLayout.json)
contains six usage arrays and table hashes, all 256 selector outcomes, nine
auxiliary variants, transport metadata and reviewed instruction-window pins.
Its exact SHA-256 is `f8f0ba8d85804187b3b063363460bc3e867bc1a147d9ae2895970a62de1a1013`.
The loader checks the complete report before parsing. Only derived metadata is
committed; no vendor firmware image is included.

```text
python tools/firmware/verify_blade16_amd_keymap.py <local binary or HEX>
```

Optional `--carrier <path>` checks the original package size and hash.
`--output <new JSON path>` creates a report without overwriting an existing
file. The standard-library verifier checks image identity, USB descriptors,
all selector values, packed records, auxiliary references and instruction
windows. Hashes reproduce the reviewed code; they do not replace semantic review.

## Regional selector and canvas

Initializer `0x5B08` reads `0x100017F0`. Its switch at `0x5B80` selects the main
pointer at `0x1000012C`. Exact `00/86` branch `0x7058` returns that same stored
byte in payload byte 0. Payload byte 1 reads the separate byte `0x100017F1`;
the handler does not write byte 2. Windows input language and payload byte 1
do not select the physical map. Numeric selectors are not assigned country names.

| Payload byte 0 | Descriptor offset | Nonzero usage records |
| --- | --- | --- |
| 0, 1, 5, 11 | `0xB169` | 86 |
| 8 | `0xBBD9` | 86 |
| 9 | `0xB739` | 86 |
| 7 | `0xCB09` | 87 |
| 12 | `0xCFD9` | 90 |
| Every other byte, including defaults 17-255 | `0xC5E1` | 87 |

Search `0x56C8` and renderer `0x6610` establish the packed eight-byte records:
record index supplies the ordinary RGB address, with record stride `row*136 +
column*8` and RGB stride `row*51 + column*3`. Auxiliary records select RGB by
their row/column fields; all references remain inside the selected main map.
The conservative union address mask contains nine holes:
`0,17,34,51,65,68,81,85,92`. A nonzero usage record alone does not prove that
all of its LED channels are populated. In particular, usage `2C` at slot 91
has zero LED fields, the main renderer skips it, and no auxiliary variant
references that RGB slot.

The saved 0528 canvas keeps its legacy coordinates. Enter canvas 66 maps by
usage `28`; backslash canvas 49 maps by `31`, with the explicit `32` alias
where `31` is absent. Supplemental canvas 70 maps to usage `64` when present.
The legacy 0528 Right Alt canvas address is 91: it maps to source usage `E6`
at physical 94, or 95 in selector 12. Canvas 94 has no independent legacy key
ownership and remains unmapped. This explicit software compatibility override
preserves the previous LampArray/imported/reactive canvas intent while correcting
its physical destination. It does not alter source records or Raw Input identity.
Other missing usages remain unmapped; the foundation makes no nearest-key guess.

## Independent standard-row transport

The report-pointer table at `0xDE6C` selects interface 2 descriptor `0xDBEA`,
94 bytes. Its vendor Feature item specifies 90 eight-bit fields and no Report
ID item. Windows therefore uses a 91-byte HID buffer with a zero prefix.
This is not SO6120's extended 374-byte report path.

Class dispatcher `0x910C` sends class `03` to `0x8044`. Command `0B` reaches
`0x85EA`, checks row below 6, end column below 17, and start at most end, then
copies RGB from payload offset `4 + (column-start)*3` to staging buffer
`0x100018D5 + row*51 + column*3`. Existing typed row payloads remain
`[00,row,start,end,RGB...]`. The firmware's separate `FE/FE/FE` clear sentinel
is outside this review's bounded row API and is not newly admitted.

Custom activation `03/0A`, payload byte 0 `05`, reaches `0x8558`, sets the
custom flag and calls `0x4884(5)`. That routine copies 306 bytes to active
buffer `0x10000C66`, consumed by renderer `0x6610`. It selects standard staging
when byte `0x100001BD` is nonzero, or when both `0x10000179` and `0x10000188`
are nonzero; otherwise it selects alternate buffer `0x10001221`. CRT descriptor
`0xE6F8` copies initialized ROM `0xE718` to RAM `0x100000C0`, including the
value 1 at ROM `0xE815` for staging-selector byte `0x100001BD`. This establishes
the standard staging default; it does not invent a Driver-mode prerequisite.
The reviewed custom activation path does not contain SO6120's conditional
staging clear. Keep the existing row-then-activation sequence.

Keyboard input is interface 0's keyboard collection and interface 1's
report-ID-1 keyboard collection. Interface 1's other reports, interface 2's
mouse/vendor Feature collection and interface 3's vendor collection are not
keyboard input authority.

## Scope and evidence status

This is exact RZ09-0528 `1532:02C6` static source evidence, independent of
RZ09-0581 `1532:02E0`. It does not attest an installed MCU, claim new physical
regional validation, or authorize hardware tests, installation or new commands.
Fresh exact-device, controller, ownership, session and applicable power checks,
restoration and stop-on-ambiguity requirements remain in force.

Legacy captured rendering/calibration assets can retain their appearance role.
They do not own command admission or regional physical-map authority. The
model-specific source proof supplies that boundary separately from recorded
physical validation.

## Regional-only input and rendering

The serialized canvas stays unchanged. Typed transient pulses additionally use
reserved canvas identities for the ISO key and Japanese Ro, Yen, Convert,
Non-convert and Katakana/Hiragana keys. Each backend resolves these identities
through its own device-selected source map. Keys absent from that selected map
produce no physical output; no proprietary usage gains a Windows input binding.

The standard HID-to-Scan-Code-Set-1 bindings were independently checked using
[Windows HidP_TranslateUsagesToI8042ScanCodes](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidpi/nf-hidpi-hidp_translateusagestoi8042scancodes)
with a data-collecting callback, without input injection or device access:
64→56, 87→73, 88→70, 89→7D, 8A→79 and 8B→7B (hexadecimal).

Spatial effects sample an additional regional cell from the nearest represented
canvas position. That is OpenBlade's color-rendering policy, not a guessed key
identity. Reactive and per-key imports leave unspecified keys black. Existing
appearance-calibration assets remain rendering data; firmware source owns command,
address, selector and input admission. The layout byte is cached only for the
lifetime of the admitted device session; brightness-only operations need no read.
