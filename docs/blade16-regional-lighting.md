# Blade 16 RZ09-0581 regional lighting

The exact SO6120 MCU 1.07 image contains six regional 6x17 keyboard descriptor
maps. The former capture-derived hole mask matches its ANSI map at `0xB345`.
Other maps put Enter at slot 48 and the regional key beside Enter at slot 64;
the ANSI mask forced both black. Usage `64` (the additional ISO key) occupies
slot 70 on the ISO maps. This is a source-backed explanation for issue #957,
not a claim of physical testing on the reporter's keyboard.

## Pinned source

| Artifact | SHA-256 |
| --- | --- |
| `SO6120_MCUv01.07.00.exe`, 1,387,888 bytes | `252618ae4cb3c865de557d198ac3b7cf1310865e529eb1daab9790675792feb7` |
| `SO6120_keyboard_FW_v1.07.00_20260810.hex` | `1306b80533ee78e82bbcdb91ab646f650a511af379ef36ad3662a1e149782386` |
| Decoded image, 57,764 bytes, base zero | `90bbe0d8dd1ca31614a2465c7c8821347b2a667606c39bedadbd59b98886344f` |

The original Razer USA Ltd. signed carrier and its exact-model package identity
were reviewed in the [MCU bridge analysis](so6120-mcu107-ec-fan-bridge.md).
The carrier was decoded as data. No vendor executable was run. USB descriptor
`0xDDB4` independently identifies `1532:02E0`.

The [embedded source report](../src/OpenBlade.Contracts/Fixtures/Blade16KeyboardLightingLayout.json)
contains all six usage arrays, descriptor hashes, the complete 256-value
selector, auxiliary RGB references and instruction-window pins. Its exact
SHA-256 is `187711408fc0667273cabb19a1cb73b57ca53f98688993ba76e7352a8f2ae0fb`.
It preserves the original report bytes; the loader checks that pin before
parsing. The report contains derived metadata, not the vendor firmware image.

Run the offline verifier against the pinned decoded image or original HEX:

```text
python tools/firmware/verify_blade16_keymap.py <local image or HEX>
```

Optional `--carrier <path>` checks the original carrier hash and size.
`--output <new JSON path>` refuses to overwrite an existing file. The verifier
checks the complete image, USB identity and Feature descriptor, table hashes,
all selector branches, auxiliary references and instruction windows using only
Python and the repository's Intel HEX decoder. Instruction hashes reproduce
reviewed code windows; they are not a replacement for the static semantic review.

## Device selection and address authority

Initialization `0x5C4C` reads RAM `0x10001990`; switch `0x5CC4` selects the
main descriptor pointer at `0x10000130`. Exact query `00/86`, branch `0x730A`,
returns that same stored byte in payload byte 0. Payload byte 1 is the separate
adjacent stored byte. The reviewed handler does not write payload byte 2.
Neither byte 1 nor Windows input language selects a physical RGB map.

| Payload byte 0 | Descriptor offset | Nonzero slots |
| --- | --- | --- |
| 0, 1, 5, 11 | `0xB345` | 86 |
| 8 | `0xBDB5` | 86 |
| 9 | `0xB915` | 86 |
| 7 | `0xCCE5` | 87 |
| 12 | `0xD1B5` | 90 |
| Every other byte, including firmware default values 17–255 | `0xC7BD` | 87 |

Country names are not inferred from numeric selectors. Each table has 102
packed eight-byte records. The first byte is the firmware key usage; record
index establishes the RGB slot. Search `0x57EC` and renderer `0x674C` use
`row*136 + column*8` for records and `row*51 + column*3` for RGB. The renderer's
nine selectable auxiliary descriptor variants address only populated slots in
the corresponding main map. The union has 93 active slots and nine holes:
`0,17,34,51,65,68,81,85,92`.

The saved canvas remains the original ANSI 6x17 coordinate contract. At the
physical projection boundary, its Enter slot 66 maps by usage `28` to slot 48
where required. Backslash slot 49 maps by usage `31`, or the explicit regional
usage `32` alias when `31` is absent. Supplemental canvas slot 70 represents
usage `64` only on layouts that contain it. Missing usages remain unmapped;
there is no nearest-key guess. This RGB alias does not establish a new Raw Input
identity. The public union mask describes physical availability, not a change
to the serialized canvas or permission to bind proprietary usage bytes to input.

## Extended-frame source proof

The interface-indexed report pointer table at `0xDF18` points interface 3 to
`0xDF00`, a 23-byte HID descriptor with 374 eight-bit Feature fields and no
Report ID item. A Windows HID buffer therefore has 375 bytes including its zero
report-ID prefix. SET_FEATURE handler `0x2460` copies 374 bytes for interface 3.
Extended dispatcher `0x7090` sends class `03` to `0x70EC`; command `0B` reads
absolute-slot RGB from internal-report offset `12 + slot*3` into staging
`0x10001A76 + slot*3`. Retain the host's 102-slot bound even though the firmware
handler accepts end indices below 120.

Standard custom activation `03/0A`, payload byte 0 `05`, reaches `0x89A0` and
calls `0x48E8`, copying 306 bytes without reordering to active RGB buffer
`0x10000CC2`. The renderer consumes that same ordering. Entering custom mode
clears staging when device-mode byte `0x100001E8` is 3 and the previous internal
effect is not 8; preserve the established activation-before-frame ordering.
This finding does not add a new device-mode prerequisite or hardware command.

The exact keyboard input collections are interface 0's keyboard collection and
interface 1's report-ID-1 keyboard collection. Exclude interface 1's consumer,
system and usage-zero collections, interface 2's mouse, and interface 3's
consumer/vendor Feature collection. An interface class alone is not sufficient
input admission.

## Evidence and scope

These maps and transport semantics are source reviewed for exact RZ09-0581
`1532:02E0`. They do not attest the installed MCU image or establish physical
regional lighting validation. RZ09-0528 `1532:02C6` has independent evidence and
must not inherit these descriptors or their validation status.

Fresh exact-device, controller, ownership, power and session admission,
stop-on-ambiguity behavior and restoration requirements remain in force.
This review grants no hardware writes, installation, firmware update, new
commands or unattended validation authority.

Existing captured rendering/calibration assets may continue to define the legacy
canvas appearance. They are not command or regional-layout authority and must
not gate hardware admission; the exact-model source proof owns that boundary.
