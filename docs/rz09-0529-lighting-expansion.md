# RZ09-0529 lighting expansion review

Reviewed 2026-11-13 against exact Synapse product 711 (`1532:02C7`). This
follow-up documents per-key keyboard animation imports, keyboard-only OpenBlade
rendering, the vendor's full-frame Vapor Chamber path, and independent VC/status
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
The VC transport row follows logical effect row 0. OpenBlade currently sends
only keyboard rows 0–5 with 61-byte `03/0B` payloads. `FF` is the payload frame
ID, not the report transaction ID. The reviewed `03/0A [5,0]` selector activates
both LED5 and LED38, while `0F/02 [0,5,8]` selects keyboard-only custom-frame
mode.

OpenBlade does not yet send the VC row. The `03/80 [0,38,0]` getter confirms
only the enabled boolean. Expanded MCU disassembly identifies a separate
`0F/82 [0,38,...]` getter for built-in VC effect parameters, but not custom pixels.
The current durable renderer baseline and restore path confirm keyboard MCU effect
parameters and brightness only. Until an exact-model restoration path covers
VC state, sending row 6 and selecting both regions could leave an unknown VC
effect behind, so VC frame mutation remains unavailable. Physical validation
is also pending.

OpenBlade renders Spectrum, Fire, Wheel, Starlight, Tidal, Reactive and Ripple
in software on the keyboard. Ambient Awareness and Audio Meter use the same
keyboard-only transport for current-user samples. Off, Static, Breathing and
Wave retain the typed MCU implementation. Imported sequences use the same
keyboard-only renderer with the exact RZ09-0529 114-slot sequence layout; they
do not include auxiliary row 6.

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

## Touchpad status and Vapor Chamber lighting

The selected product configuration enables `hasVCLed` and the
`functionLighting.config.touchpadStatus` indicator. These are separate features.
The selected vendor UI calls the former **Vapor Chamber Lighting**. Synapse's
frame engine includes LED38 in full-matrix frames, but OpenBlade does not yet
admit those writes because it cannot restore the prior VC effect. This does not
independently identify the owner-reported Under-glass label or establish a
Touchpad RGB zone.

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
38. It does not expose a VC per-LED RGB read or restore operation. The generic
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
VC and touchpad RGB remain unavailable until exact addressing and
independent restoration semantics are established.

The selected enabled-setting task passes selector 1 while the getter uses 0.
Targeted MCU review of `8A08–8A5C` and `8AD8–8ADC` establishes that both access
the same inverted global flag at `0x1000017A`, without persistence or a bank
copy. OpenBlade reads the baseline and independently confirms each changed
enabled value. OpenBlade's current VC controls offer enabled state, not sleep,
RGB or effect selection. Synapse's full-frame behavior is separate from this
independent enabled flag.

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

Service IPC is 83, machine profile schema 27, override schema 10 and exported
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
