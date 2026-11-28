# RZ09-0529 lighting expansion review

Reviewed 2026-11-13 against exact Synapse product 711 (`1532:02C7`). This
follow-up documents per-key keyboard animation imports, OpenBlade host rendering
on the keyboard and Vapor Chamber (VC, LED38), and independent VC/status
controls. Physical validation is pending. Other source-reviewed controls are listed in
[lighting coverage](source-reviewed-lighting.md).

## Host-rendered effects

The exact [lighting-engine manifest](https://apps.razer.com/synapse/lighting-engine/manifest/DeviceManifest_711_0.json)
has 1,692,915 bytes and SHA-256
`FDD5D817BF28F152B3DF498AC6E908291993BBCC6939BE4D86F94AD74F52A9F1`.
It selects `rzDevice25LedMatrixSkipSetEffect` on interface 2, not the middleware's
`rzDevice25LedMatrixBlade2` class. The selected engine class must be traced
separately from the middleware encoder and activation sequence.

All nine `ledConfig_0_*` layouts declare a 7-by-19 device transport and an
8-by-18 logical effect canvas. Transport row 6 has eighteen mapped addresses,
columns 1 through 18, at logical row 0. It is not an empty padding row. Layout
IDs are 1, 3, 4, 6, 7, 8, 9, 12 and 16. Mapped-address counts are respectively
116, 119, 119, 119, 119, 116, 116, 123 and 119. Each inspected layout has unique,
in-bounds transport addresses and no keyboard scan-code binding that points to
multiple logical positions within that layout.

The MCU resolves the apparent geometry discrepancy: rows 0–5 are the 6-by-19
keyboard; row 6 belongs to LED38, the auxiliary VC region. The selected Synapse
frame engine sends every transport row in its frame and then selects its custom
frame effect. Its selected engine method loops through `eventData.numRow`; the
manifest declares a 7-by-19 device transport and 8-by-18 logical effect canvas.
The VC transport row follows logical effect row 0. Keyboard rows 0–5 and VC
row 6 use 61-byte `03/0B` payloads. `FF` is the payload frame ID, not the
report transaction ID. The reviewed `03/0A [5,0]` selector activates both LED5
and LED38; OpenBlade does not use it. `0F/02 [0,5,8]` selects keyboard-only
custom-frame mode and `0F/02 [0,38,8]` selects VC-only custom-frame mode.
`0F/02 [0,0,8]` also switches the LED4 logo and is not used.

OpenBlade renders Spectrum, Fire, Wheel, Starlight, Tidal, Reactive and Ripple
in software. Ambient Awareness and Audio Meter use the same transport for
current-user samples. Off, Static, Breathing and Wave retain the typed MCU
implementation. Imported sequences use the exact RZ09-0529 114-slot keyboard
layout.

Spectrum, Fire, Wheel, Starlight, Tidal, Ripple, Ambient Awareness and Audio
Meter also drive the VC when its baseline is restorable (see
[Vapor Chamber frames](#vapor-chamber-frames)). Grid effects evaluate the VC at
renderer row 0, columns 0–17. Ripple uses logical key-position coordinates
(column, 0). Starlight gives each VC LED its own slot after the 114 keyboard
slots. Ambient Awareness and Audio Meter use their uniform sample color. Reactive
and imported or edited per-key sequences stay keyboard-only. Switching to one of
them while OpenBlade owns the VC first restores the VC.

Each submitted frame uses six acknowledged keyboard rows, or seven with the VC
row. The HID session waits 30 ms before reading each acknowledgment, so the row
exchanges take at least 180 ms (210 ms with the VC) before accounting for HID
I/O. For imported sequences, the animation scheduler waits at least 50 ms after
each update and samples the sequence against elapsed time, so short timeline
frames can be skipped. Key-triggered duplicate suppression compares the keyboard
and VC rows together.

The input map keeps 94 ordinary keyboard scan bindings common to all nine
layouts. It excludes vendor `dkm` identifiers, auxiliary-row inputs and regional
Enter/backslash/Alt disagreements. Spatial Ripple additionally needs a common
logical position; two transport slots have conflicting regional positions and
do not become guessed Ripple origins. Reactive can still illuminate a known
transport slot without a spatial coordinate.

The Lighting page includes a built-in per-key color editor. It uses the reviewed
RZ09-0529 keyboard slots, starts with one color, and lets users select a key and
change its color. Razer Chroma keyboard animation imports also use the common
scan-code map. Unmapped source cells are omitted with an import warning; editor
and imported frames remain keyboard-only and use the existing durable renderer
restoration path.

Before host entry, the service saves a complete independently read built-in
baseline and exact raw active brightness to a protected, atomic journal. Unknown
external custom pixels or hidden Spectrum palette state cannot supply that
baseline. Shutdown restores through the MCU-aware built-in setter, confirms all
parameters, then restores and reads back exact raw bank0 brightness. A pending
journal on restart marks the renderer faulted; it never authorizes startup replay.
Partial submission or uncertain confirmation stops writes and retains recovery
provenance. See the [command and descriptor proof](../evidence/source-reviewed-blades/2026-11-13-rz09-0529-keyboard-frames.json).

## Vapor Chamber frames

The VC restore contract comes from the exact KI8110 MCU 1.07.00 image (Intel
HEX SHA-256 prefix `18392850`, decoded image SHA-256
`C0EBB3B40D3AC763A50D7301457D73F1A84A3E2C951730454D848B13CA3F3578`). The class
`0F` handler at `0x9400` dispatches the get table at `0x9524` and the set table
at `0x96B2`. Effect records are `0x40` bytes at `0x100015D6 + 0x40 * i`; index 0
is LED5, 1 is LED4 and 2 is LED38. Selector 0 is the volatile active bank.

`0F/82 [0,38,0,…]` returns `[0,38,mode,…]` with the same layout as the keyboard
getter. `0F/02 [0,38,mode,…]` uses the keyboard setter layouts:

| Mode | Setter arguments | Readback | Restorable |
| --- | --- | --- | --- |
| Off | `[0,38,0]` | Mode only | Yes |
| Static | `[0,38,1,0,0,1,R,G,B]` | `[5]=1`, `[6..8]` RGB | Yes |
| Breathing | `[0,38,2,0,0,count,colors…]`, count 1 or 2, else random | `[5]` count and colors | Yes, including random |
| Wave | `[0,38,4,direction]`, 1 left, 2 right | `[3]` direction, `[4]` speed 40 | Yes |
| Spectrum (3), Starlight (7), mode 9, custom (8) | — | Incomplete | No |

Spectrum depends on a hidden gate and writes a hidden flag. Starlight stores a
parameter the getter does not return. Mode 9 stores parameters outside the
record, and custom pixels cannot be read. No VC setter changes record
brightness, so the saved VC destination always uses brightness 100. From custom
mode 8, every restorable setter clears the custom-ownership flag and skips its
same-mode shortcuts, so restoration needs no bridge effect.

OpenBlade reads the VC only for an effect that drives it, or to return a VC it
selected; Reactive, imported sequences and built-in effects never read it. The
first restorable record seen for a keyboard baseline becomes the VC restore
destination. An unrestorable record keeps frames keyboard-only, with no VC
write. A rejected or malformed reply on a usable session also keeps frames
keyboard-only. It stops further VC reads for that host and HID identity until
the service restarts or the identity changes. A failure that leaves the session
unusable stops the request before any write. A read that never reached the MCU,
such as a shared command-gate refusal, leaves only that request keyboard-only
and records nothing about the VC. A running renderer is replaced normally, and
the next request reads the VC again. Opening an RZ09-0529 session makes the same read once for each
admitted host and HID identity. A rejection there drains that session and
opens one fresh session, and later sessions for the identity skip the read and
stay keyboard-only. A gate refusal or cancellation there settles nothing:
cancellation returns a canceled open, a refusal opens the session without the
read, and the next open reads again.

OpenBlade takes the VC only from a read made under the same operation lock,
while the VC reads custom mode 8 under OpenBlade's selection record or still
equals the restore destination. Ambient Awareness and Audio Meter choose the VC
when they start but select it with their first sample, so that sample reads it
again. Before `[0,38,8]` can be submitted, the journal records the destination
and moves to schema 2; keyboard-only journals keep schema 1 byte for byte. Only
this selection record lets a restore write the VC, so a keyboard-only owner
never overwrites another controller's VC frame. The record is removed after a
confirmed restore, or when the VC no longer reads mode 8 because another
controller changed it.

When the VC participates, every frame also sends transport row 6. Its column 0
is black and columns 1–18 carry logical VC columns 0–17, as in Synapse's own
frames. The KI8110 1.07.00 row-6 handler stores transport columns 0–17 in VC
slots 0–17 and drops column 18. VC slot 0 therefore shows the black column 0,
and logical VC column 17 is not shown, exactly as with Synapse's frames.
Physical validation decides whether that offset is visible. The VC keeps its
own record brightness, so OpenBlade scales VC frame colors by the keyboard
brightness percentage instead of writing VC brightness. First activation sends
`[0,5,8]`, confirms keyboard mode 8, then sends `[0,38,8]` and confirms VC mode
8. Any failure after a possible submission is indeterminate: writes stop
without replay and the journal is kept.

Every restore point (renderer release, shutdown, a built-in effect choice and
switching to a keyboard-only effect) reads the VC first while the selection
record exists. OpenBlade restores the saved VC effect, with confirming
readback, only while the VC still reads mode 8. Any other mode means another
controller changed it, so OpenBlade leaves it unchanged. A failed or
unconfirmed restore faults the owner and keeps the record for recovery. A
confirmed low-power boundary keeps the journal without restoring, as for the
keyboard. Only RZ09-0529 has this LED38 contract: a journal that holds an LED38
record for any other product is invalid, and no other model's restore sends a
region-38 command.

## Windows Dynamic Lighting

KI8110 1.07.00 also exposes a HID LampArray collection (usage page `0x59`,
usage `0x01`) at image offset `0xEE30`. Its report handler at `0xA784` serves
LampArrayAttributes, LampAttributes, LampMultiUpdate, LampRangeUpdate and
LampArrayControl. AutonomousMode lives at `0x100001C9` and starts at 1.
LampArrayControl writes it at `0xAA7A`. A keyboard-event branch at `0x7FA8`
also sets it back to 1 when it is 0, clears custom flags 0 and 2 (keyboard and
VC) and persists. The
handler covers LED38 as well as the keyboard: lamps below the count minus 18
go to the keyboard LampArray buffer at `0x10001264` and the last 18 lamps go to
the VC LampArray buffer at `0x1000145C`. Neither buffer is the staging buffer
that `03/0B` rows fill (`0x10001B80` for the keyboard, `0x10001CD6` for the VC).

The custom-frame copy helper at `0x4B80` chooses the source per region. When
AutonomousMode is 0 and either the `0F/10` policy byte `0x10000179` or
`0x10000188` is 0 (both are 0 by default, and `0x10000188` is only ever
stored as 0), it copies the LampArray buffer instead of staging. The `0F/02`
custom arm at `0x9CBC` sets mode 8 and the custom flag without checking
AutonomousMode, so `0F/82` still reports mode 8.

This is the same hazard that keeps RZ09-0509 and RZ09-0510 frames unadmitted,
and it applies to the keyboard frames already admitted here as well as to the
VC:

- While Windows Dynamic Lighting holds AutonomousMode 0, OpenBlade rows land
  in staging but the keyboard and VC show the LampArray buffers. OpenBlade has
  no reviewed command that reads AutonomousMode, so the hidden output is not
  detectable.
- Each LampMultiUpdate or LampRangeUpdate in that state sets mode 8 and the
  custom flag on any region that lacks them, then persists. After Windows
  sends an update, both regions read mode 8, the baseline is unrestorable and
  host entry is refused. An OpenBlade restore confirmed while Windows still
  holds control is replaced by mode 8 at its next update.
- Returning AutonomousMode to 1 sets each region that is not already Spectrum
  to a persisted Spectrum and clears its custom flag (`0xAAB6`–`0xAAF4`).
- `0F/02` Off, Static, Breathing and Wave still clear the custom flag. The
  `0F/02` Spectrum arm acts only while AutonomousMode is 1.

The LampArray handler never writes the staging buffers or OpenBlade's journal,
and it never changes a region that already reads mode 8 with its flag set.
The addresses and window hashes are in the
[command and descriptor proof](../evidence/source-reviewed-blades/2026-11-13-rz09-0529-keyboard-frames.json).
Admission is unchanged while the maintainer decides how to gate it.

## Touchpad status and Vapor Chamber lighting

The selected product configuration enables `hasVCLed` and the
`functionLighting.config.touchpadStatus` indicator. These are separate features.
The selected vendor UI calls the former **Vapor Chamber Lighting**. Synapse's
frame engine includes LED38 in full-matrix frames. OpenBlade includes it in host
frames only with a restorable VC baseline, as described in
[Vapor Chamber frames](#vapor-chamber-frames). This does not independently
identify the owner-reported Under-glass label or establish a Touchpad RGB zone.

The touchpad indicator follows Windows touchpad enabled status. Its typed
notification is `00/59`, with `[0, 1, enabled]`. Targeted MCU review establishes
the independent `00/D9 [0,1,0]` getter for the same boolean global. OpenBlade
exposes this as a status indicator, not a Windows touchpad switch or RGB zone.

The selected Blade2 backend provides these VC operations:

| Operation | Command | Arguments |
| --- | --- | --- |
| Read enabled | `03/80` | `[0, 38, 0]` |
| Set enabled | `03/00` | `[profile, 38, enabled]` |
| Set sleep policy | `03/21` | `[0, 38, mode, duration]` |

The exact Product 711 Blade2 module also provides VC brightness through region
38. It does not expose a VC per-LED RGB read or restore operation; the MCU's
region-38 effect record supplies OpenBlade's VC restoration instead. The generic
Product 711 middleware base contains LED RGB helper methods, but the selected
Blade2 module and checked Product 711 UI bundles do not use them for the VC or
touchpad. Those generic methods do not establish this model's LED addresses,
RGB semantics, or independent readback, so they are not an admitted control
contract. A sweep of all 88 JavaScript chunks listed by the pinned Product 711
UI entrypoint found no `getLedRGB`/`setLedRGB` callsites or per-LED address map.
The touchpad UI chunk only reads `trackpadStatus` and opens Windows touchpad
settings. The exact middleware chain uses the generic RGB setters for unrelated
knob indicators and volume/mute dials; its VC methods only read or set VC state
and region-38 brightness, and its touchpad method changes multifunction status.
Hashes and the review scope are recorded in the
[source evidence](../evidence/source-reviewed-blades/2026-11-13-rz09-0529-keyboard-frames.json).
Independent OpenRazer support for `1532:02C7` likewise declares a 6-by-19
keyboard matrix and keyboard effect/frame operations, but no VC or touchpad RGB
interface ([upstream change](https://github.com/openrazer/openrazer/pull/2551)).
This corroborates the keyboard scope; it does not rule out undocumented commands
or provide a zone read/restore contract.

The reviewed RZ09-0529 firmware catalog lists BIOS, EC and keyboard MCU packages,
with no separate touchpad firmware payload. The pinned EC 1.06 carrier contains
an EC image whose reset and startup instructions identify Andes NDS32 code.
Its inspected USB descriptor identifies a separate `1532:F003` EC endpoint;
that endpoint rejects class `0F`, so the keyboard MCU's VC commands cannot be
transferred to it. The bounded EC review did not identify a touchpad RGB path.
Touchpad RGB remains unavailable until exact addressing and independent
restoration semantics are established.

The selected enabled-setting task passes selector 1 while the getter uses 0.
Targeted MCU review of `8A08–8A5C` and `8AD8–8ADC` establishes that both access
the same inverted global flag at `0x1000017A`, without persistence or a bank
copy. OpenBlade reads the baseline and independently confirms each changed
enabled value. OpenBlade's VC controls offer enabled state and host-frame
colors, not sleep policy or built-in effect selection. Host frames are separate
from this independent enabled flag.

The Fn+V task supplies an enabled-only operation: `setVcLedState(1, enabled)`.
There is no source-backed requirement to also write the sleep policy for that
operation. The ordinary settings editor writes both fields, but that is not a
firmware prerequisite. Initialization and the post-write UI update use saved
intent rather than `getVcLedState()`; OpenBlade instead uses the actual getter
before and after a write.

The MCU also implements `03/A1 [0,38,0,0]` for effective sleep settings. However,
`03/21` changes additional power-specific RAM settings and, with selector1,
persistence shadows that this getter does not return. Mode0/1 also retain the
previous duration. Sleep-policy mutation remains unavailable because the entire
original state cannot yet be independently saved and restored. This limitation
does not apply to the independent VC enabled flag.

Service IPC is 85, machine profile schema 27, override schema 10 and exported
profile schema 18. Old documents cannot introduce non-null zone-control intent;
ordinary prior settings migrate without opting in to the new fields.

## Source pins

Middleware paths below are relative to
`https://apps.razer.com/synapse/products/711/mw/`; UI paths are relative to
`https://apps.razer.com/synapse/products/711/ui/static/js/`.

| Source | SHA-256 |
| --- | --- |
| MW `main.1f8e0c604f19af3ea24c.js` | `C8C0C794D6D01867CD9C6020869808428B81CB98AE297E46EC700F3B935BFF7E` |
| MW `rzDevice25.c8da1ead63e6cf002d32.js` | `02C55B78B3EABDCC104D725C64C94CADE500FA78D29713ACE5FEF520D243EBB9` |
| MW `rzDevice25LedMatrixBlade2.d678cb5b99e6382aebe0.js` | `62356152B59E6D172AF8D07677534EBA0BF9DBFEBA973AFE51BEC8437D031FD8` |
| MW `6737.c63928b0e4fd1b7673b4.js` | `3EF16C0BEB5E8399CDEE51E9995A717E029CDF2125324735440B68A34D2D7B88` |
| UI `main.a649ed49.js` | `4B42EF4995470865B7D02587B85724247B886BDF2376EB6A78DA38BB9B6CBF99` |
| UI `9742.aa50375b.chunk.js` | `06C467C6C5C76345D271390D5D7D5163E23DEAEA5C89858793F9E95BA9F409D9` |
| UI `trans-en.ff66e286.chunk.js` | `20C9D7536009DD1B2F99B76D4CA4F4CD8EE3B16CF36F3AF5DBEE643998FF7FCD` |

No laptop commands, installations, service changes, or vendor native code were
executed during this review.
