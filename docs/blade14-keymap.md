# Blade 14 keyboard map from exact MCU firmware

The exact RZ09-0530 (`1532:02C5`) PI4110 keyboard MCU contains the physical
keyboard-to-light descriptor maps. This review recovers those maps from the
original signed MCU package as inert data. It does not infer a lighting map
from a BIOS scan table, the platform EC, another model, or a matrix size.

The [source report](../evidence/source-reviewed-blades/2026-11-12-pi4110-keymap.json)
records all 96 slots, holes, table hashes, selector groups, common bindings,
instruction-window hashes and USB input descriptors. These are source-reviewed
maps without physical validation or installed-MCU attestation. The source pins
are the same independently reviewed package and image used by
[compact Wave readback](compact-wave-readback.md).

## Exact source and extraction

| Artifact | SHA-256 |
| --- | --- |
| PI4110 MCU 1.04.00 carrier | `aed7039be5cc4ec3f166a2728b0c1b90839bae72c8a04150056654f64f20581f` |
| `PI4110_keyboard_FW_v1.04.00_20250918.hex` | `4787f3c382d57c165dd2de099147b678070d0251e0febb5b6cc5a7902048bcd3` |
| Decoded image, 52,612 bytes | `1ac3f05486d0bca5fbbb1a4a52b874943ad491a00afd298b8d6b857a49d98f11` |

Run the offline verifier against a locally acquired pinned HEX or decoded image:

    python tools/firmware/verify_blade14_keymap.py <local HEX or binary>

An optional `--carrier <original carrier>` checks its exact hash and size.
`--output <new JSON file>` refuses to overwrite an existing file. The verifier
never executes a carrier, opens a device, contacts a service, or invokes Ghidra.
It validates the whole image and exact USB identity before interpreting tables.
Its instruction hashes reproduce the reviewed windows; semantic findings come
from static instruction review, not a general-purpose disassembler inside the
verifier.

## Regional maps and complete selector coverage

Application initialization calls `0x5D24`. It reads the stored edition byte at
`0x10001870`, then the compact switch at `0x5D98` selects the descriptor pointer
stored at `0x1000012C`.

| Firmware edition byte | Map offset | Nonzero usage bytes |
| --- | --- | --- |
| 0, 1, 5, 8, 9, 11 | `0xAFE3` | 79 |
| 12 | `0xB63B` | 83 |
| 2, 3, 4, 6, 7, 10 and every other byte value | `0xB313` | 80 |

Each map has six rows of sixteen packed eight-byte records. Its first byte is
the firmware key usage; zeros identify holes. Descriptor order gives the lighting
address: `row = index / 16`, `column = index % 16`. Record bytes 2 and 3 are not
address authority: Space and regional extra keys demonstrate differing values.

Function `0x58C8` searches the first byte across the 6x16 records and returns its
loop row and column. Renderer `0x682C` uses that same record order to select RGB
input and physical LED driver channels. No country names are assigned to the
edition values. Reserved/proprietary usage bytes remain separate from standard
USB keyboard usages.

When edition is unavailable, only usage-to-slot pairs identical in all three maps
are admitted: 74 firmware usage bytes, including 71 ordinary Windows scan-code
bindings in the production fixture. The three remaining common bytes (`81`,
`D2` and `F0`) are not automatically admitted as Windows input bindings. The
intersection covers every firmware selector branch, including its default.
Region-specific keys remain unbound without a confirmed applicable map.

Exact `00/86` returns the same edition byte at payload byte 0 and an adjacent
stored byte at payload byte 1. The second byte remains opaque. The common
intersection does not require this query.

## Link to custom RGB commands

Standard `03/0B` reaches `0x84BE`, checks `row < 6` and `endColumn < 16`, then
copies RGB to `0x10001955` with a 48-byte row stride and 3-byte column stride.
Standard `03/0A [05,00]` selects custom mode through `0x82B6` and effect-5 branch
`0x83C6`. Handoff `0x4A8C` copies 288 bytes into active RGB buffer `0x10000D5A`
without reordering. Renderer `0x682C` uses `row*48 + column*3` for RGB and
`row*128 + column*8` for the corresponding physical descriptor.

The handoff selects staged source `0x10001955` when
`*0x100001B9 != 0 || (*0x10000179 != 0 && *0x10000188 != 0)`; its alternate
source is `0x100012D6`. The application CRT copies ROM `0xCC84` to RAM
`0x100000C0` for 256 bytes: initialized ROM byte `0xCD7D = 1` therefore
selects the standard staged source through `0x100001B9` without a mode write.
Later edition initialization `0x5F4C` writes capability-record offsets +4
and +2, preserving that +1 flag. The alternate-source controls belong to the
extended-frame/USB path; they do not establish a Driver-mode prerequisite for
the admitted standard rows and activation. No additional mode command is needed
by this reviewed standard-source path.
This map review establishes address ordering and retains
the existing source-reviewed custom-frame contract. It admits no guessed flag
semantics, new mode writes, or physical color confirmation. These raw flags are
not a new prerequisite until their owning command contract establishes one.

The separate 6x22 HID table at `0xBE44` belongs to startup animation `0x634C`,
which translates into the active descriptors. Using it directly as the 6x16
command map would assign incorrect columns.

## Input collection boundary

USB descriptor table `0xC454` gives four interface-indexed report pointers and
lengths. Configuration `0xC474` declares the interfaces and endpoints:

- Interface 00 has one standard keyboard application collection (usage page 1,
  usage 6), with no report ID.
- Interface 01 has five application collections. Only its first collection,
  report ID 1, is the standard keyboard. Consumer, system-control and usage-zero
  collections are excluded.
- Interface 02 is a mouse.
- Interface 03 uses vendor page `0x59` despite its interface keyboard protocol,
  and is excluded from keyboard input binding.

Windows input binding restricts the exact verified host model, VID/PID,
reviewed interface/collection and typed RawInput keyboard packets. The service
retains fresh per-write hardware admission. A matching interface class alone
is insufficient.

This review does not establish RZ09-0482/029D applicability. Its independently
pinned MCU is a different image and requires its own descriptor/command trace.
No updater or hardware action was performed during this investigation.
