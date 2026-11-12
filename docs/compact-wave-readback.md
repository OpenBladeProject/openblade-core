# Compact Wave lighting readback

Issue [#945](https://github.com/OSSBlade/openblade-core/issues/945) reports
keyboard and logo controls blocked on Blade 14 (2025), RZ09-0530 (`1532:02C5`).
Its diagnostic export records successful `0F/82 [01 05 00]` queries returning
`01 05 04`, successful brightness readback `01 05 2D`, and rejection before any
lighting setter. The reply identifies Wave but contains no direction byte.

## Reviewed command contract

The existing pinned
[blade-control getter](https://github.com/jdsgnrinfo/blade-control/blob/92beedc82e6e378cb71dac276e5820c8ea84247d/src/BladeControl.Core/Services/LightingService.cs#L182)
requests `0F/82 [01 05 00]` and decodes only the effect ID in byte 2.
Its Wave setter separately sends `0F/02 [01 05 04 01]`. The
[packet constructor](https://github.com/jdsgnrinfo/blade-control/blob/92beedc82e6e378cb71dac276e5820c8ea84247d/src/BladeControl.Core/Hardware/RazerHidPacket.cs#L27)
sets the request data size from the argument count. Neither source establishes
that omitting direction from a Wave write preserves the existing direction.

The pinned
[razer-ctl getter](https://github.com/sqmagellan/razer-ctl/blob/9c66e7055a0e31f5ec25c16185e66fde1f50cf4a/librazer/src/command.rs#L342)
documents PID `029F` returning additional effect parameters, including direction,
while its
[decoder tests](https://github.com/sqmagellan/razer-ctl/blob/9c66e7055a0e31f5ec25c16185e66fde1f50cf4a/librazer/src/command.rs#L644)
also accept the compact Wave effect ID. A three-byte request does not imply
that every firmware returns only three bytes. The admitted `03/8A` single-zone
color getter belongs to `0255`.

## Exact keyboard MCU review

The lighting command handler is in the keyboard MCU, rather than the BIOS or
the separate platform EC. The exact RZ09-0530
[official MCU 1.04.00 carrier](https://dl.razerzone.com/drivers/Razer%20Blade%2014%E2%80%9D%20%282025%29%20%7C%20RZ09-0530/PI4110-MCUv01.04.00.exe)
has a valid Razer USA Ltd. signature. It was extracted as data, without executing
the updater. Pins and instruction-site assertions are retained in the
[source-review result](../evidence/source-reviewed-blades/2026-11-12-pi4110-lighting-readback.json).

| Artifact | SHA-256 |
| --- | --- |
| Original carrier, 1,439,888 bytes | `aed7039be5cc4ec3f166a2728b0c1b90839bae72c8a04150056654f64f20581f` |
| `PI4110_keyboard_FW_v1.04.00_20250918.hex` | `4787f3c382d57c165dd2de099147b678070d0251e0febb5b6cc5a7902048bcd3` |
| Strictly decoded image, 52,612 bytes | `1ac3f05486d0bca5fbbb1a4a52b874943ad491a00afd298b8d6b857a49d98f11` |

The image's USB identity is `1532:02C5` at `0xC1C8`. Its fixed 90-byte USB
receive/send loops are at `0x27CA` and `0x26DC`. Class dispatch at `0x9044`
reaches the `0F` handler at `0x8678`; command `82` reaches `0x876E`.
The Wave branch at `0x8818–0x8832` writes effect ID, direction and speed into
payload bytes 2, 3 and 4, while retaining the incoming header's data size.
A three-byte request therefore leaves direction outside the declared payload.
Requesting `0F/82 [01 05 00 00]` exposes direction within a four-byte reply.
The reviewed path neither checks a three-byte-only size nor mutates lighting.
Other admitted built-in modes leave byte 3 zero after the common payload clear.

The Wave setter at `0x8AA2–0x8B0A` reads payload byte 3 unconditionally and
stores it as direction at `0x8AFC–0x8AFE`. Omitting it supplies zero from the
report padding; there is no preserve-current-direction form. The setter writes
speed 40 internally at `0x8B02–0x8B04`, so no speed argument is needed.
OpenBlade retains its reviewed four-byte Wave SET and requests the fourth GET
byte on the models reviewed below. It does not request the fifth byte, which would expose
speed that the current typed contract does not represent.

RZ09-0482 (`1532:029D`) independently has the same contract in its
[official PI490 MCU 1.04.00 package](https://dl.razerzone.com/drivers/Razer%20Blade%2014%E2%80%9D%20%282023%29%20%7C%20RZ09-0482x/RazerUpdater_v1.1.1.3_PI490-MCUv010400v2.exe):

| Artifact | SHA-256 |
| --- | --- |
| Original carrier, 1,330,264 bytes | `9653d899701a889f3e533e8e4a6733869ca3b4bc5c66942b6512d3d848c40e51` |
| `PI490_keyboard_FW_v1.04.00_20230608.hex` | `42adb76bcb84203237284ed652955e6cdc1ebfb86de4fedb4b780cdf364be54c` |
| Decoded image, 59,456 bytes | `dc4d17d4e552922c7712225301bd31474d61e4b12908e00ffa02da4e2fccbe05` |

Its Wave getter at `0x979A–0x97B6` reads direction into payload byte 3;
the setter consumes it at `0x9BBE–0x9BC0` and writes speed 40 at `0x9BC4`.
The fixed 90-byte transport, class dispatch and return path preserve data size.
The [PI490 result](../evidence/source-reviewed-blades/2026-11-12-pi490-lighting-readback.json)
records the separately checked offsets and branches. This establishes the
four-byte GET on `029D` as well as `02C5`; it does not establish it on `029F`
or `02A0`, which keep their existing request shape.

Run `tools/firmware/verify_blade_lighting_readback.py <local HEX or decoded image> --carrier <local official carrier>`
to reproduce the bounded hash, identity,
instruction and branch assertions. Vendor files are not stored in Git. This
static review does not attest the user's installed MCU or prove physical output;
normal exact-device admission and independent response validation still apply.

## Shared handling

The experimental lighting backend recognizes an exact three-byte Wave reply
as a known effect with unavailable direction. It returns no complete
`LightingSettings` and records `WaveDirectionReadbackUnavailable`, rather than
reporting Rightward or a malformed response. Extended replies retain their
selector and padding validation. Wire direction 2 is a known opposite Wave
direction, observed read-only as `WaveDirectionNotRestorable`: it permits a
direct built-in destination but supplies no frame restoration baseline. The
existing built-in setter remains restricted to direction 1. Zero, unknown
direction values, unknown effects and malformed parameters still block writes.

An admitted built-in destination is applied directly after fresh write admission.
Compact Wave is not a complete restoration baseline for a host-rendered frame,
so those requests stop before any setter. Wave destinations still send the
existing four-byte setter, independently verify the effect ID and exact raw
brightness, and keep direction unavailable in readback. A mismatching effect,
failed read or ambiguous submission pauses writes without replay.
The compact-only final confirmation requires the exact three-byte payload;
an extended reply reporting direction 2 cannot confirm a requested direction 1.

The service asks the admitted backend whether the selected setting requires a
host renderer. Successful firmware-driven effects do not require host renderer
ownership. Host-rendered effects retain their ownership checks, including after
profile changes, lifecycle restoration and bounded recovery.

The shared compact-response and renderer handling applies to RZ09-0530 (`02C5`),
RZ09-0482 (`029D`), RZ09-0483 (`029F`) and RZ09-0484 (`02A0`). Regression fixtures
cover their shared handling; the reported compact response is physical evidence
only for the issue's `02C5`. RZ09-0328 (`0255`) does not admit Wave, and
RZ09-0301 (`0245`) exposes independent brightness/logo controls without this
effect getter. The separately validated `02C6` and `02E0` backends retain their
own readback contracts. No new firmware command or model capability is added.
