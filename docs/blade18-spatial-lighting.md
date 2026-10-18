# Blade 18 spatial lighting source recovery

PR [#786](https://github.com/OpenBladeProject/openblade-core/pull/786) adds
experimental Reactive, Ripple and Tidal for exact RZ09-0484 / 1532:02A0.
The map and effect semantics come from Synapse. No further capture is needed
to implement them. OpenBlade appearance and restoration on this laptop remain
unvalidated; this is source-backed experimental support.

## Exact-model key map

The [product-672 UI bundle](https://apps.razer.com/synapse/products/672/ui/static/js/main.0d842862.js)
has SHA-256
B44F2BA2062F49886D4EFD9F68D784E1FB159AD43753EA17BE7B931F58377991.
Its product definitions and chunk routing select nine regional layouts
(1, 3, 4, 6, 7, 8, 9, 12, 16), each in editions 0 and 128.
The embedded Blade18KeyboardLightingLayout.json records all 18 DEVICECONFIG
URLs, hashes, byte lengths and offsets. Its SHA-256 is
446751B434F3B3EE7E55DE49D3BB93FEDEE4D48468BCE85EF19497D41DEF05EC.
The loader verifies these fixture bytes before admitting the map.

For example, [layout 1](https://apps.razer.com/synapse/products/672/ui/static/js/7920.590d31ec.chunk.js)
has SHA-256
D38B8896F07F5DC80B6898B65B85526EF0B982B666657CD9B74A64BACEE07720;
its DEVICECONFIG starts at UTF-8 byte 70151.

The recovered definitions provide 87 renderable slots and 77 scan-code bindings
common to every regional layout and both editions. Physical positions use a
15-column, seven-row logical grid; the transport remains six rows of 16 RGB
cells. These grids are different: Down is logical (13,6), output slot 95.
Reactive fades a pressed slot; Ripple expands from its recovered position.
Tidal and Ripple can illuminate all recovered slots, including keys whose
input binding is not common across layouts. The remaining transport holes stay black.

Enter, backslash, left/right Alt, right Ctrl and regional extra keys have
layout-dependent bindings. OpenBlade omits those input origins until it can
identify the physical layout; Windows input language is not sufficient.
Left Alt's rendering position uses layout 1's (3,5); Japanese layout 12 places
that slot at (4,5). This is a documented geometry approximation.

The independent [device manifest](https://apps.razer.com/synapse/lighting-engine/manifest/DeviceManifest_672_0.json)
corroborates identity, matrix and input mapping, but contains conflicting
entries. All 18 UI definitions agree that extended scan 0x53 (Delete) maps to
output slot 15; the manifest aliases it to Insert. The coherent UI definitions
are authoritative here. Their input and output matrices also resolve regional
Enter/backslash and right-Fn differences. This map admits only the three spatial
effects; Sequence and individual-key color editing remain unavailable.

## Effect behavior

The [lighting-engine bundle](https://apps.razer.com/synapse/lighting-engine/static/js/main.a383ea9e.js)
has SHA-256
C82C7074E5DA8ABCFFBFF95F23A6B962C1E14B04586715B9D4B7A6F11FE9E6D2
and 1,830,359 bytes. Relevant UTF-8 offsets are:

| Offset / module | Definition |
| --- | --- |
| 1076453 / 6394 | Reactive Short, Medium, Long: 500, 1500, 2000 ms |
| 1421921 / 8905 | Ripple width 200, speed 25, key-triggered |
| 1424849 / 8905 | Tidal angle 160 degrees, width 100, speed 10, split enabled |
| 1434169 / 8905 | Center from model override or floored grid midpoint |
| 1079692 / 6394 | Direction 0/1 maps to native 0/1 |
| 415936 / 2578 | Tidal gradient stops and native parameters |

Passive disassembly of RzLightingEngineApi_v4.0.55.0.dll, SHA-256
CD9FC2A61FF920B9B73E1F5E27632020E2F8586F443E426A875AD3B042B714FA,
resolves the parameter units and raster behavior. The DLL was never loaded or
executed. Relevant RVAs are Ripple setup 0x53690 and render 0x53c60; Tidal
setup 0x675c0, centers 0x67830, wave initialization 0x67f40, advance 0x680f0
and render 0x68300; gradient 0x692e0 and random colors 0x69600.

Reactive fades linearly over the selected duration. Ripple uses a two-cell
ring moving at 25 logical cells per second. Tidal projects absolute,
center-relative positions at 160 degrees around center (7,3), with extents
(8,4). It is a moving split gradient, with a five-second period derived from
width/(2 * speed). Outward and inward reverse its travel. A wave starts black
until its head reaches the position, matching native startup arrival checks.

Fixed-color Tidal uses stops 0,10,25,40,60,75,90,100 percent; random-color
Tidal uses 0,15,30,40,65,70,90,100. Both assign black, black, primary, black,
black, secondary, black, black. Random colors use the native 24-color palette:
RGB channels 0/128/255, excluding grayscale. OpenBlade uses deterministic
per-wave choices; it does not reproduce Synapse's random seed. Continuous
sampling approximates native pixel rasterization, so this is not a claim of
pixel-for-pixel equivalence. No other laptop's captured effect frames or key
mask are used.

## Runtime and verification

The effects share the admitted SET-only six-row frame transport, 20 FPS budget,
entry activation, fresh per-row admission, baseline restoration and
stop-on-ambiguity behavior. Input pulses update bounded memory; they do not
write HID or acquire the firmware operation queue. Pulses from another known
keyboard model are excluded before slot dispatch. Replacing or retiring an
effect drains the active frame and stops its loop.

Offline tests cover fixture admission, scan-code mappings, regional exclusions,
Reactive fading, Ripple geometry, Tidal timing and direction, deterministic
random colors, memory-only pulses, ownership and restoration. This work adds
no firmware commands, driver dependencies, installations or hardware writes.
