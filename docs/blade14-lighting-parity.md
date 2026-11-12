# Blade 14 lighting effects

Issue [#945](https://github.com/OpenBladeProject/openblade-core/issues/945) confirms
keyboard and logo control on Blade 14 (2025) after the Wave readback fix, and
requests the missing keyboard effects.

## Exact-model frame contract

The reviewed OpenRazer revision is
`477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d`. Its
[keyboard definitions](https://github.com/openrazer/openrazer/blob/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d/daemon/openrazer_daemon/hardware/keyboards.py#L2202)
independently declare a 6×16 custom matrix for RZ09-0482 (`029D`, lines
2202–2216), RZ09-0483 (`029F`, 2259–2273), and
[RZ09-0530](https://github.com/openrazer/openrazer/blob/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d/daemon/openrazer_daemon/hardware/keyboards.py#L2354)
(`02C5`, 2354–2368).

The exact-model
[row dispatch](https://github.com/openrazer/openrazer/blob/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d/driver/razerkbd_driver.c#L4669)
uses standard custom rows, transaction `FF`, and request/response exchanges.
The [encoder](https://github.com/openrazer/openrazer/blob/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d/driver/razerchromacommon.c#L450)
declares a 70-byte `03/0B` payload: `FF,row,00,0F`, 48 RGB bytes, and 18
zero padding bytes. Rows are 0–5 and columns 0–15. The two Blade 14 backends use
this exact padded form. The existing Blade 16 and Blade 18 52-byte row contracts
remain as separately established by their reviewed sources.

[Custom activation](https://github.com/openrazer/openrazer/blob/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d/driver/razerkbd_driver.c#L3998)
uses transaction `FF` and
[`03/0A [05,00]`](https://github.com/openrazer/openrazer/blob/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d/driver/razerchromacommon.c#L390).
The source checks response envelopes and status; it establishes no payload echo
or RGB getter. OpenBlade serializes acknowledged rows and checks current write
admission before each submission. An incomplete frame pauses writes, including
when a later guard refuses a row after earlier rows succeeded.

## Effects and ownership

Static uses uniform rows. Ambient Awareness and Audio Meter receive one sampled
color through the existing typed uniform-frame path. Color Breathing, Leftward
Wave, Fire, Wheel, Starlight and Tidal use deterministic software patterns over
these models' own 96 addresses. Their periods and colors are OpenBlade software
choices; they do not reproduce another model's recorded output. Animation runs
at up to 20 frames per second, skips missed frames, and drains before replacement.
Native random Breathing and Rightward Wave retain their existing commands.
Blade 14 (2025) also offers Reactive, Ripple and imported Keyboard .chroma
sequences through its [firmware-derived common key map](blade14-keymap.md).
Regional bindings that disagree across the three firmware maps remain unavailable.
Blade 14 (2023) and Blade 16 (2023) retain Reactive/Ripple as observed-only.

Frames retain the complete confirmed built-in baseline and exact raw brightness
for restoration. Dark wake stages the content before raising brightness. Firmware
RGB stays unavailable; retained intent and owned renderer state do not become
color readback. Recovery keeps frame contents unknown and never replays them.
Compact or alternate-direction Wave remains an incomplete baseline; the user can
explicitly choose a readable built-in effect before entering host rendering.

This source review establishes command applicability, software rendering and
failure handling. Physical color output and on-device timing remain untested.
The two older models retain the address-grid effects above; their own complete
input-to-light maps remain outside this review. The current service IPC advances
from 79 to 80 for the new Blade 14 layout ID; protocol 51 remains read-only and
strips the new layout. Deploy the coordinated product rather than a tray-only patch.
