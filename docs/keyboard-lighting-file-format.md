# OpenBlade keyboard lighting files

OpenBlade keyboard lighting files are UTF-8 JSON documents with the `.oblight`
extension. Version 1 stores one portable keyboard effect preset. Importing a
preset never grants a hardware capability: the effect must already be admitted
for the exact connected Blade and its firmware.

The media type is `application/vnd.openblade.keyboard-lighting+json`.

The format is data-only. It cannot contain scripts, plug-ins, external assets,
file paths, USB identities, HID reports, firmware commands, or device mappings.
OpenBlade rejects unknown fields, duplicate fields, invalid enum values,
unsupported schema versions, and files larger than 64 KiB.

## Example

```json
{
  "format": "openblade-keyboard-lighting",
  "schemaVersion": 1,
  "metadata": {
    "name": "Northern lights",
    "author": "Example",
    "description": "A two-color breathing preset.",
    "revision": 1
  },
  "effect": {
    "kind": "breathing",
    "brightnessPercent": 70,
    "colorMode": "dual",
    "primaryColor": "#47E10C",
    "secondaryColor": "#8A2BE2"
  }
}
```

The complete example is in
[`docs/examples/northern-lights.oblight`](examples/northern-lights.oblight).
The normative structure is
[`docs/schemas/openblade-keyboard-lighting-v1.schema.json`](schemas/openblade-keyboard-lighting-v1.schema.json).
Runtime semantic validation remains authoritative.

## Envelope

| Field | Requirement |
| --- | --- |
| `format` | Must be `openblade-keyboard-lighting`. |
| `schemaVersion` | Must be `1`. Future versions are rejected clearly. |
| `metadata.name` | Required, 1–80 characters after trimming. |
| `metadata.author` | Optional; `null` or 1–80 characters. |
| `metadata.description` | Optional; `null` or 1–500 characters. |
| `metadata.revision` | Positive integer chosen by the preset author. |
| `effect` | One effect object from the table below. |

Colors use `#RRGGBB` sRGB notation. Every effect except `off` requires
`brightnessPercent` from 1 through 100.

## Effects

The effect names follow Razer Chroma terminology where the semantics match,
then add OpenBlade's admitted host-rendered effects.

| `kind` | Additional fields |
| --- | --- |
| `off` | None. |
| `static` | `color`. |
| `spectrumCycling` | None. |
| `breathing` | `colorMode`: `single`, `dual`, or `random`; matching nullable `primaryColor` and `secondaryColor`. |
| `reactive` | `color`; `duration`: `short`, `medium`, or `long`. |
| `tidal` | `primaryColor`, `secondaryColor`, `randomColors`, and `direction`. |
| `starlight` | `colorMode`, matching colors, and `duration`. |
| `wave` | `direction`: `leftward` or `rightward`. |
| `wheel` | `direction`: `clockwise` or `counterclockwise`. |
| `ripple` | `color`. |
| `ambientAwareness` | `region`: `full`, `left`, `top`, `right`, or `bottom`. |
| `audioMeter` | `colorBoost` from 1 to 4 in 0.25 steps; `decayMilliseconds` from 0 to 2000 in 250 ms steps. |
| `fire` | None. |

An imported preset applies through the same typed service operation as a manual
Lighting-page edit. The confirmation dialog states that it will apply, and the
result remains an unsaved change for the active power profile until the user
saves or discards it. The file does not change lid-logo lighting.

## Razer `.chroma` compatibility

Razer's [MIT-licensed Chroma editor](https://github.com/razerofficial/CChromaEditor/blob/6ce8b24db8e4e00ddafe027de7589b2a0b4dc618/LICENSE)
and its [binary `.chroma` serializer](https://github.com/razerofficial/CChromaEditor/blob/6ce8b24db8e4e00ddafe027de7589b2a0b4dc618/CChromaEditorLibrary/Animation2D.cpp#L370-L461)
and [matching reader](https://github.com/razerofficial/CChromaEditor/blob/6ce8b24db8e4e00ddafe027de7589b2a0b4dc618/CChromaEditorLibrary/ChromaSDKPlugin.cpp#L1142-L1356)
are the main compatibility references. They provide useful fixed-grid animation
frames and per-frame durations, and Razer documents sequential client rendering.
However, the binary container has no magic signature, embedded grid dimensions,
layout identifier, metadata envelope, or schema. Its generic keyboard canvas
also does not establish a reviewed mapping to either OpenBlade model's exact
protocol order.

The importer accepts official binary version 1 files with device type `2D` and
device `Keyboard` (6×22) or `KeyboardExtended` (8×24). It verifies the complete
little-endian structure, frame count, finite non-negative durations, Win32
`COLORREF` values, exact length, and a 5 MiB size limit. It never loads the
Chroma SDK or Synapse.

OpenBlade imports a `.chroma` file only when every cell in every frame has the
same color. All-black files become `off`; any other color becomes `static` at
100% brightness. This conversion is independent of key layout and preserves
the result exactly. Per-key colors or colors that change between frames fail
with an explicit unsupported-animation error rather than being cropped,
averaged, or silently remapped.

Strict JSON remains the canonical format. A later schema can add bounded frames
using Chroma-compatible timing semantics after an exact-model mapping and
sequence renderer are admitted. No layout is silently copied between
RZ09-0581 and RZ09-0528.

Other formats informed the design but are not safe drop-in standards:

- [Polychromatic sequences](https://docs.polychromatic.app/config/effects/sequence/)
  use JSON frames and looping, but their device-name mapping is not OpenBlade
  admission evidence.
- [OpenRGB profiles](https://gitlab.com/CalcProgrammer1/OpenRGB/-/blob/release_candidate_1.0rc3/ProfileManager.cpp)
  are binary device snapshots tied to controller identity and SDK versions.
- [QMK RGB Matrix](https://docs.qmk.fm/features/rgb_matrix) separates matrix,
  LED, and physical positions well, but custom effects are compiled C code.
- [SignalRGB Lightscripts](https://docs.signalrgb.com/developer/) are executable
  HTML and JavaScript and are outside this import boundary.
- [Windows Dynamic Lighting](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/dynamic-lighting-devices)
  standardizes HID LampArray devices, not a portable effect file.

## Compatibility and versioning

The file `schemaVersion` is independent of OpenBlade profile-storage and IPC
versions. Version 1 readers never reinterpret an unknown future document.
Adding a new optional or required field, effect kind, frame model, or layout
target requires a new isolated schema reader when the old meaning cannot be
preserved exactly.
