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
B5C03C722EBA0F57C09D211E4C162F35B7E37A68F0B089E43625C526AD43A1D8.
The loader verifies these fixture bytes before admitting the map.

For example, [layout 1](https://apps.razer.com/synapse/products/672/ui/static/js/7920.590d31ec.chunk.js)
has SHA-256
D38B8896F07F5DC80B6898B65B85526EF0B982B666657CD9B74A64BACEE07720;
its DEVICECONFIG starts at UTF-8 byte 70151.

The recovered definitions provide 87 output slots across the regional keyboards.
OpenBlade retains every region's own input map and geometry, including the
Japanese bottom row, ISO extra key, Enter, backslash and Alt keys. Physical positions use a
15-column, seven-row logical grid; the transport remains six rows of 16 RGB
cells. These grids are different: Down is logical (13,6), output slot 95.
Reactive fades a pressed slot; Ripple expands from its recovered position.
Tidal and Ripple use the selected physical region's positions and leave
absent keys and transport holes black.

The physical layout comes from the exact-model edition getter described below.
Windows input language is not used to choose a hardware layout. If the getter is
unavailable or returns a layout without reviewed definitions, OpenBlade maps only
scan codes whose output agrees wherever Synapse declares them. This also covers
unique ISO/Japanese scan codes. Enter, backslash and left/right Alt remain
unmapped in that fallback because their output addresses conflict. Unknown-region
rendering retains the reviewed union geometry; slot 85 uses layout 1's (3,5)
approximation. A confirmed Japanese layout selects its actual (4,5) position.

## Physical region getter

Product 672's middleware selects its physical region from
getEdition().jsonData.keyboardLayout. The request is 00/86, declared length 3,
with three zero argument bytes. Its exactly three response bytes are physical
layout, edition and firmware ID. The separate Set Edition command is 00/06;
OpenBlade never sends it. This is a source-backed observational contract, not a
physical observation on this laptop.

The exact-model inheritance chain is pinned below. No vendor code was executed.

| Source under Synapse product 672 middleware | SHA-256 / source location |
| --- | --- |
| [main.57245df0507455a5119e.js](https://apps.razer.com/synapse/products/672/mw/main.57245df0507455a5119e.js) | D523F5080784B07CB1888C39E636004626E073E2967B85999498030102F85FAE; offset 160725 selects the Blade class |
| [rzDevice25LedMatrixBlade.dced3c2efdc9705a346c.js](https://apps.razer.com/synapse/products/672/mw/rzDevice25LedMatrixBlade.dced3c2efdc9705a346c.js) | 56B9309797CB177012726D2EAA1C2BFD43906E9ADED6AC39488487440E72036C; module 77127 extends 71513 |
| [rzDevice25LedMatrix.21f461fce761cff01be3.js](https://apps.razer.com/synapse/products/672/mw/rzDevice25LedMatrix.21f461fce761cff01be3.js) | 8F8C464818EF7E7AD75F9B18594E74A47D911F5F869C91DF3923AC2880F7F7F6; module 71513 extends 74503 at byte 988 |
| [rzDevice25.85ce8530211c29f3c304.js](https://apps.razer.com/synapse/products/672/mw/rzDevice25.85ce8530211c29f3c304.js) | DFB3E3612D36B20910AF02E61A17ED64A1FFD3781E09C63BAAEE7CBEFDEBCA24; getter 128257, parser 2350, command 18020, report builder 116408 |
| [6737.e50bb836b878190315d7.js](https://apps.razer.com/synapse/products/672/mw/6737.e50bb836b878190315d7.js) | 651F63DCC0EFC92B0FD7496EE70751707B5F650F20AF5C01C3255B61817C1997; module 34938 at byte 1884893 selects the returned region |

Neither intermediate class overrides getEdition. Only exact PID 02A0 initializes
this metadata after a unique control interface is admitted. Only the nine pinned
regions are selected; advertised IDs 5 and 11 lack reviewed DEVICECONFIG data.
A failed or unknown read leaves the fallback map available and does not disable
unrelated controls. Cancellation still propagates. A poisoned session cannot be
accepted as healthy fallback. The service publishes the nullable region to the
Session Agent; losing device verification clears its effective input region.
The existing 02E0 transition contract and shared raw-query diagnostics exclusion
for 00/86 are unchanged; experimental diagnostics record only the decoded region.

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

The optional physical-region capability uses service IPC version 69; the Session
Agent IPC is unchanged. Deployment requires the coordinated full installation.

Offline tests cover fixture admission, scan-code mappings, regional selection and fallback,
Reactive fading, Ripple geometry, Tidal timing and direction, deterministic
random colors, memory-only pulses, ownership and restoration. This work adds
no firmware commands, driver dependencies, installations or hardware writes.
