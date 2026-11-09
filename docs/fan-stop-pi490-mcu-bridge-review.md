# RZ09-0482 PI490 keyboard MCU: fan-command bridge

Reviewed offline on 2026-11-08 against core
`10caac37c5868ee8bbc4a54a392141facc5b5a34`. The exact keyboard MCU exposes
ordinary fan/mode requests and a gated generic EC-bus bridge. The named
`0D/0D` duty setter and `0D/8D` getter are unsupported in this image.
This is partial command-path evidence, **not a Stop/Auto contract**: the
EC-side implementation, independent actual PWM/tach readings and confirmed
Automatic restoration remain unidentified. No runtime support changes.

## Input and analysis boundary

The official [RZ09-0482 keyboard update](https://mysupport.razer.com/app/answers/detail/a_id/13877/)
and its [pinned carrier](https://dl.razerzone.com/drivers/Razer%20Blade%2014%E2%80%9D%20%282023%29%20%7C%20RZ09-0482x/RazerUpdater_v1.1.1.3_PI490-MCUv010400v2.exe)
were acquired in [the preceding package review](fan-stop-blade14-missing-ec-evidence.md#rz09-0482-keyboard-mcu-is-a-separate-component).
Reuse its valid Razer signature and exact package binding; no updater was run.

| Input | Bytes | SHA-256 |
|---|---:|---|
| Carrier | 1,330,264 | `9653d899701a889f3e533e8e4a6733869ca3b4bc5c66942b6512d3d848c40e51` |
| `PI490_keyboard_FW_v1.04.00_20230608.hex` | 166,752 | `42adb76bcb84203237284ed652955e6cdc1ebfb86de4fedb4b780cdf364be54c` |
| Strictly decoded image, base zero, gaps `FF` | 59,456 | `dc4d17d4e552922c7712225301bd31474d61e4b12908e00ffa02da4e2fccbe05` |

The inert update batch selects `1532:029D`; the USB device descriptor at
`E3CC` independently contains those IDs. Boot vector words are
`100013A8/0000019D`; application vectors at `2000` are
`10001B90/00002461`. They establish an ARM/Thumb application layout here,
not the laptop EC's ISA or a precise MCU silicon identity. All addresses below
are zero-based image offsets, except explicitly identified MCU RAM addresses.

Ghidra 12.1.3 analyzed this image as `ARM:LE:32:Cortex`, with application
vectors and then computed switch destinations seeded. A compact switch helper
at `D3AC..D3C6` consumes inline count/table bytes; treating them as instructions
initially produced spurious targets. The reviewed destinations below are
decoded directly from those bytes and checked against the dispatch instructions.
Additional speculative callback seeds produced unrelated decompiler warnings;
their pseudocode is not used as proof. The original byte windows and retained
instructions are authoritative evidence for this bounded review, rather than
an assumption that every decompiled function is correct or every path found.

## Named duty command: unsupported in this exact image

Normal frame dispatch reads command class from byte 6 at `B74E`, invokes the
compact switch at `B752`, and routes class `0D` through `B786 -> A488`.
That table has count `10` hex, base `B757`; its class-13 destination is `B786`.
The thermal handler reads command byte 7 at `A490`.

- Setter `0D/0D` misses the implemented command comparisons and reaches
  `A4AC -> A582 -> A79E`. The latter stores response status `05`.
- Getter `0D/8D` takes the switch at `A4C2`: subtracting `85` gives index 8,
  whose table destination is also `A582`, hence the same unsupported result.

The switch helper takes the count byte at call address plus 4, then a table
whose entries are doubled byte offsets relative to call address plus 5.
Indices at or above the count use the final default entry. The reproduction
script checks both this mapping and the handler's original instruction sites.
No live probe is needed to establish these dispatch results for this image.

## Ordinary fan/mode path and the model-specific difference

The thermal cache is at MCU RAM `100002F8`; pending request bits are at
`10000210`. The request frame is at `100015C8`, with payload beginning at
frame offset 8. These are MCU state, not direct Windows or EC RAM addresses.

| HID request | Handler behavior | Queued EC-bus operation |
|---|---|---|
| `0D/01`, fan 1 or 2 | `A510..A544` stores payload byte 2 unchanged at cache `+11/+12`, sets pending bit 2/3 | `2848` / `2872` call write helper `335C`, one byte to index `1B/1C` |
| `0D/02`, selector channel 0, 1 or 2 | `A596..A5C2` stores payload byte 2 at cache `+8`, byte 3 at `+9`, sets pending bit 0 | `278E` writes one mode byte to index `1A`; `27A4` writes value `05` to index `0A` |
| `0D/81` | Returns shared fan cache bytes; SET and worker refresh both write them | Worker separately refreshes them from indexes `1B/1C` at `27E0/2814` |
| `0D/82` | Returns cached selector/auxiliary state; cache byte `+0A == 80` yields auxiliary `02` | Worker separately refreshes selector from index `1A` at `275A` |

The queued worker uses slave-address byte `B0`, write helper `335C`, and read
helper `33A4`. The write helper constructs the slave/index/data transfer;
the read helper constructs slave/index followed by the read address and copies
received bytes only on bus status `0C`. This establishes the MCU-side bridge,
without identifying the EC's internal RAM behind those bus indexes.

In particular, **the examined PI490 `0D/02` setter has no local mode-byte
upper-bound check** after validating its channel. It forwards that byte through
the cache to index `1A`. The `mode <= 7` restriction previously found in
[SO6120 MCU 1.07](so6120-mcu107-ec-fan-bridge.md) must not be copied here.
This does not establish what PI490's EC does with `FE`, whether zero RPM is
clamped, what constitutes actual duty, or how to restore Auto safely.
The fan setter similarly has no local RPM floor in the examined handler;
the downstream EC may still impose one. Matching GET cache bytes is neither
an independently fresh physical observation nor a duty/tach confirmation.

## Generic bridge exists; fan semantics still missing

Class `00` dispatch also implements a generic bridge gated by MCU RAM byte
`10000214`. `00/22` stores a Boolean gate value (`B604..B612`), and `00/A2`
returns it (`B5FC..B600`). When enabled, `00/21` reaches write helper `335C`
at `B5EA`, while `00/A1` reaches read helper `33A4` at `B5C6`. Arguments
include slave byte `B0`, index from payload byte 2, count from byte 1 and a
buffer at payload `+0A`; bus status is copied to payload byte 4. When the gate
is clear these paths return status `05`.

This is a concrete host-to-EC investigation lead. It establishes no fan-specific
address bounds, reservation, exclusive ownership, immutable descriptor or
actual-PWM/Auto contract. It must not become an exposed raw-command feature or
be used to guess another model's register map. No claim about every upstream
frame-length check or the gate's complete lifetime is made by this partial trace.

## Reproduction and next useful source

Ignored artifacts are in `artifacts/model-expansion/pi490-mcu-bridge/`.
`verify.py` independently checks HEX checksums/address bounds/non-overlap/EOF,
the decoded image hash, vector words, USB IDs, both compact switch tables,
literal RAM addresses and material instruction sites. It emits `verified.json`
and original-byte/instruction windows in `verified-proof.txt`. The Ghidra seed,
switch-refinement and dump scripts and their logs are retained alongside them.
These checks execute only analysis tools, never the MCU bytes or vendor software.

The next missing source is the **PI490 EC implementation** reached through
these bus indexes, or a complete exact-model vendor API that establishes zero
duty, independent actual readings for every channel, completion/ownership and
Automatic restoration. The MCU trace narrows that search; it does not supply
the missing EC version or grant implementation or hardware-test admission.
