# Blade 18 Synapse Observer evidence

Tracking: [#739](https://github.com/OpenBladeProject/openblade-core/issues/739).

The [Observer bundle](https://github.com/user-attachments/files/33499731/blade-evidence-20261015-1459.zip)
identifies Razer `Blade 18 - RZ09-0484`, USB `1532:02A0`, BIOS `2.09`.
BIOS and regional SKU are provenance, not new admission restrictions.
The ZIP SHA-256 is `2833BE5F8D02445050872A4E577648A8CB5337A4455CB9E68335B77B8E2736F2`;
the collector is `0.2.8+1046bac54a76292762ca94cd376371667780b90f`.

## Retained observations

The command collection has usage page/usage `01/02` and a 91-byte feature
report. The fixture in
`tests/OpenBlade.Protocol.Tests/Fixtures/Blade18ObserverRequests.json` preserves
only the 12 reviewed request buffers, operation IDs, and source provenance.
No opaque response prefixes, interface paths, or frame RGB bytes are copied.

| Selection | Commands and arguments |
| --- | --- |
| Silent | `07/8C [00 00]`, `07/8F [00]`, `07/0F [00]`, `0D/02 [01 01 05 00]`, `0D/02 [01 02 05 00]` |
| Return to Custom | `07/8C [00 00]`, `0D/02 [01 01 04 00]`, `0D/02 [01 02 04 00]`, `07/8F [00]`, `07/0F [00]`, `0D/07 [01 01 03]`, `0D/07 [01 02 02]` |

The performance markers report a starting and restored selection of CPU Boost,
GPU High. The retained `0D/07` requests independently establish that Synapse sent
CPU level 3 and GPU level 2 on this model. Their successful API completion does
not prove firmware application or independent restoration. The four semantic
response summaries acknowledge the two Silent and two Custom mode setters;
they do not contain independent `0D/87` level readback.

All 16 response bodies were omitted. Four semantic summaries describe 90-byte
GET completions with missing final padding; the existing HID transport already
handles a 90-byte completion only within its report-ID and report-validation
contract. No response validation is relaxed here. The coverage draft labels
whole-byte `07/0F [00]` under full charge; that classification is not imported.
This command controls the shared Power Mode Control byte.

The recorder retained 4,096 observations and rejected 1,976 more. Its observation
stream ends at 75.8 seconds; later lighting and display action windows begin
several minutes afterward. Missing reports do not prove absent capabilities.
There are no special-key observations or probes, independent firmware-level
restoration checks, or OpenBlade lifecycle tests in this bundle.

## Exact Synapse source review

The following official product 672 files were read as data on 2026-10-15.
No vendor JavaScript was executed. Hashes cover the complete downloaded files.

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| [main.57245df0507455a5119e.js](https://apps.razer.com/synapse/products/672/mw/main.57245df0507455a5119e.js) | 168852 | `D523F5080784B07CB1888C39E636004626E073E2967B85999498030102F85FAE` |
| [rzDevice25.85ce8530211c29f3c304.js](https://apps.razer.com/synapse/products/672/mw/rzDevice25.85ce8530211c29f3c304.js) | 272259 | `DFB3E3612D36B20910AF02E61A17ED64A1FFD3781E09C63BAAEE7CBEFDEBCA24` |
| [6737.e50bb836b878190315d7.js](https://apps.razer.com/synapse/products/672/mw/6737.e50bb836b878190315d7.js) | 1957301 | `651F63DCC0EFC92B0FD7496EE70751707B5F650F20AF5C01C3255B61817C1997` |
| [rzDevice25LedMatrixBlade.dced3c2efdc9705a346c.js](https://apps.razer.com/synapse/products/672/mw/rzDevice25LedMatrixBlade.dced3c2efdc9705a346c.js) | 13089 | `56B9309797CB177012726D2EAA1C2BFD43906E9ADED6AC39488487440E72036C` |

Zero-based byte offsets in these pinned files locate the relevant definitions:

- Main selects `rzDevice25LedMatrixBlade` near byte 160698 and enables Custom
  performance near 161839. The backend inherits the base device at byte 1250.
- The policy's numeric level enum near 908669 maps Low/Medium/High/Extreme to
  0/1/2/3. OpenBlade calls CPU level 3 Boost.
- The base defines the CPU and GPU `0D/87` and `0D/07` tuples near 71401 and
  71523. Their GET decoders read payload byte 2 near 64561 and 65416.
- The policy applies independent CPU and GPU settings near 1559363. There is
  no explicit per-model allowed-combination table in these inspected files.

## Resulting support and remaining gaps

OpenBlade adds exactly the observed Boost/High combination to this model's
existing eight Custom combinations. High/High and Boost with Low or Medium GPU
remain unavailable. Both initial state reads and recovery can now represent
Boost/High. The setter chooses an admitted intermediate combination, confirms
each change, and pauses on uncertain submission or readback. For example,
Medium/Low to Boost/High sets GPU High before CPU Boost; it does not reset the
preset. Existing exact-device, controller, power, ownership, and safety checks
continue to apply to every write.

A production-provider regression also exposed a shared Custom-state failure: the
Maximum Fans query allowed a variable response but supplied no bounded payload
validator, so the real HID session rejected it before submission. The query now
supplies the transport bound and retains its existing strict one-byte bitmask
semantic check. This fixes the same path on RZ09-0483 and RZ09-0484.

The main bundle's quick-effect catalog near byte 148129 also names Ambient
Awareness, Audio Meter, Fire, Reactive, Ripple, Starlight, Wheel, and Tidal.
These are preserved in `ObservedLightingEffects` and diagnostics. They do not
enable additional lighting commands, host frames, or a physical key map.
Existing brightness, built-in effects, uniform Static, and logo support retain
their earlier evidence classifications.

The product defines fixed-fan ranges for Balanced (2200-3800), Silent
(3700-5300), and Custom (1700-4300), but the existing
[exact-model owner report](https://github.com/sqmagellan/razer-ctl/issues/1)
says requested RPM was ignored. Diagnostics retain this conflict; fixed targets
remain unavailable until it is resolved. The bundle does not resolve it.

Battery limits, HyperBoost, special keys, EC/MCU access, native brightness,
spatial lighting, and power/lifecycle behavior gain no new write admission.
Windows display inventory supplies useful panel metadata, but user-reported
refresh/VRR/DRR selections do not validate mutation backends. This submission
does not promote the model to physically validated support. The next concrete
validation is a typed Boost/High round trip on this exact model, with saved
baseline, independent readback, exact restoration, and fresh admission. Sleep
and resume remain a separate attended step when needed.
