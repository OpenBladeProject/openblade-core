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

OpenBlade adds the observed Boost/High and Boost/Low pairs to the existing eight
Custom combinations. High/High and Boost/Medium remain unavailable. Initial
reads and recovery can represent both admitted Boost pairs. The setter chooses
the shortest admitted path, confirms each change, and pauses on uncertain
submission or readback. Medium/Low or High/Low to Boost/High takes two level
writes through Boost/Low. High/Medium to Boost/High needs three through CPU
Medium because either two-write ordering crosses an unadmitted combination.
This follows the reviewed software contract; it does not claim firmware needs
CPU Medium. The preset is not reset. Existing exact-device, controller, power,
ownership, and safety checks continue to apply to every write.

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
## Follow-up bundle: 2026-10-16

The [second Observer bundle](https://github.com/user-attachments/files/33530069/blade-evidence-20261016-063420.zip)
has SHA-256 `8251A08A69CB7BDF503B137C86DEAEFDD851F2B5EB63F746B1896C98688A1BF6`
and collector `0.2.10+6c70d0f03ce07048e1e2a331da4f3d571bbcbfbf`.
The exact model, HID identity, and BIOS are unchanged. It contains 83,915
observations, 180 complete retained requests, and twelve semantic thermal
acknowledgements. All 201 response bodies were omitted. No records were rejected,
but the observer reports two dropped queue events near the end. The sanitized
requests are in `tests/OpenBlade.Protocol.Tests/Fixtures/Blade18ObserverFollowupRequests.json`.

| Complete user-reported action | Starting, target, and restored selection | Retained request evidence |
| --- | --- | --- |
| Performance | Silent, Custom Boost/High, Silent | `0D/02` mode 4 and 5 for both channels |
| Fans | 3800, 3000, 3800 RPM | `0D/01 [01,zone,1E]`, then `[01,zone,26]` |
| Charge limit | 80%, 50%, 80% | `07/12 [B2]`, then `[D0]` |
| Keyboard lighting | Wheel clockwise, Fire, Wheel clockwise | Matrix setup and redacted frames; no readable effect/frame result |
| CPU level | Boost, Medium, Boost | `0D/07 [01,01,01]`, then `[01,01,03]` |
| GPU level | High, Low, High | `0D/07 [01,02,00]`, then `[01,02,02]`; no CPU write within the action window |
| Keyboard brightness | 100%, 25%, 100% | `03/03 [01,05,40]`, then `[01,05,FF]`; one transient slider value is also retained |

The GPU action adds the source-backed experimental Boost/Low pair. It supplies
no Boost/Medium or High/High evidence. Successful calls and reported restoration
still do not establish independent firmware application or restoration.

All 22 retained Custom GETs are CPU `0D/87 [01,01,00]`. The pinned product 672
policy helper `getAdvFanMode(e,t=1)` near byte 1873321 supplies profile 1, which
the base getter near 142589 copies into its request. The GPU getter uses the
same default-profile construction. OpenBlade now uses `[01,01,00]` and
`[01,02,00]` for this model. The CPU request is observed; the GPU request is
source-backed and was not retained. Other profiles keep their separately
admitted upstream shape. This does not prove profile 0 fails. The thermal-mode
`0D/82` queries remain unchanged: there is no corresponding retained query or
active exact-product source call to justify a change.

Before the fan action, Synapse enters Balanced fixed mode with
`0D/02 [01,zone,00,01]`. The owner also reports adjustable RPM only in Balanced
in [#739](https://github.com/OpenBladeProject/openblade-core/issues/739).
The 53 retained `0D/88 [01,01,00]` actual-RPM queries have no readable responses;
there is no `0D/81` stored-target query. The safe fixed-fan backend requires an
exact target baseline and independent target readback. These semantics and the
earlier report of ignored RPM remain unresolved, so fixed-fan writes stay
unavailable. A future implementation must retain the Balanced-only model scope.

The retained reports do not establish a Maximum Fans enable request
`07/0F [02]`; privacy redaction is not evidence that it never occurred.
Whole-byte `07/0F [00]` is not temporary-full-charge evidence. Raw CPU level 4 occurs outside action
windows: the pinned policy uses it as a temporary refresh sentinel near byte
1637237 and in CPU optimization near 1490935. It is not another admitted normal
Custom level and is not copied as a prerequisite write.

Advanced Chroma streaming has only a starting marker and no associated reports.
Logo lighting and Fn/special-key observations are absent. Function-row and
Gaming Mode activities remain pending; battery protection and temporary full
charge were marked unavailable. These states do not establish hardware absence.
The seven recorded actions need no blanket repeat. Independent typed readback,
restoration, and relevant lifecycle checks remain separate OpenBlade validation.
