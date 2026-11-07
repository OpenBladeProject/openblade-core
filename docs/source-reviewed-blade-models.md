# Source-reviewed additional Blade models

OpenBlade officially supports the eleven exact-model profiles below using reviewed
Synapse definitions. Physical validation is a separate evidence status and remains
pending for these implementations. Source review does not establish measured fan
behavior, successful restoration on a laptop, or another model's capabilities.
The [support guide](experimental-blade-support.md) lists the earlier five
source-reviewed models and their different feature sets.

## 2024/2025 exact identity and initial controls

| Laptop | Exact Windows product name | USB identity | Synapse product | Initial controls |
| --- | --- | --- | --- | --- |
| Blade 18 (2025), RZ09-0529 | `Blade 18 - RZ09-0529` | `1532:02C7` | 711 | Balanced, Silent, Turbo, Maximum Power, Battery Saver, Custom Low/Medium/High, Automatic fans, bounded charge limits; no Maximum Fans |
| Blade 14 (2024), RZ09-0508 | `Blade 14 - RZ09-0508` | `1532:02B6` | 694 | Balanced, Silent, Turbo, Custom Low/Medium/High with CPU Boost, Automatic fans, Maximum Fans, bounded charge limits |
| Blade 16 (2024), RZ09-0510 | `Blade 16 - RZ09-0510` | `1532:02B7` | 695 | Balanced, Silent, Turbo, Custom Low/Medium/High with CPU Boost, Automatic fans, Maximum Fans, bounded charge limits |
| Blade 18 (2024), RZ09-0509 | `Blade 18 - RZ09-0509` | `1532:02B8` | 696 | Balanced, Silent, Turbo, Custom Low/Medium/High, Automatic fans, Maximum Fans, bounded charge limits |

All four offer battery Balanced. Other listed presets require confirmed full-power
AC, except Battery Saver on Blade 18 (2025), which is a battery mode. Turbo is
firmware mode 1; Maximum Power on product 711 is mode 2. Those modes remain
distinct. Battery Balanced uses mode 6. Custom CPU and GPU levels include Low (0), Medium (1) and High (2).
Blade 14 (2024) and Blade 16 (2024) additionally support CPU Boost (3), established
by their exact-model CPU selector and direct thermal setter path. The two Blade 18
models remain bounded to CPU levels 0–2; their native overclocking UI labelled
Boost does not establish firmware level 3.

Charge protection offers 50, 55, 60, 65, 70, 75 and 80 percent. The application's
100% selection disables protection; it does not admit enabled thresholds from
81% through 100% or intermediate one-percent values. Readback and restoration
retain the actual saved threshold and protection flag separately.

Maximum Fans is available only on products 694, 695 and 696, in Custom. Its
whole-byte Power Mode Control write requires all other settings to be clear.
Leaving Custom clears an owned Maximum Fans setting through the guarded cleanup
path. Disable-only cleanup is also admitted on battery when Windows and firmware
agree on the power source; enabling Maximum Fans still requires full-power AC.
Automatic handback is supported; arbitrary fixed targets and fan curves
are not admitted. The vendor reads mode-specific fan ranges at runtime, and its
UI and middleware definitions need further reconciliation before those writes
can be implemented safely. If the current fan mode is manual, preset and Custom
changes are blocked until an explicit Automatic handback is confirmed. This is
a software admission boundary: the backend cannot yet confirm and restore the
mode-specific manual targets, rather than evidence of a firmware prerequisite.

## 2021/2022 exact identity and initial controls

| Laptop | Exact Windows product name | USB identity | Synapse product | Initial controls |
| --- | --- | --- | --- | --- |
| Blade 14 (2022), RZ09-0427 | `Blade 14 (2022) - RZ09-0427` | `1532:028C` | 652 | Balanced on AC/battery, Silent and Custom on full-power AC, CPU 0–3 including Boost, GPU 0–2, Automatic fans, bounded charge limits |
| Blade 15 (2022), RZ09-0421 | `Blade 15 (2022) - RZ09-0421` | `1532:028A` | 650 | Balanced on AC/battery, Silent and Custom on full-power AC, CPU 0–3 including Boost, GPU 0–2, Automatic fans, bounded charge limits |
| Blade 17 (2022), RZ09-0423 | `Blade 17 (2022) - RZ09-0423` | `1532:028B` | 651 | Balanced on AC/battery, Silent and Custom on full-power AC, CPU 0–3 including Boost, GPU 0–2, Automatic fans, bounded charge limits |
| Blade 14 (2021), RZ09-0370 | `Blade 14 - RZ09-0370` | `1532:0270` | 624 | Balanced and Custom on full-power AC only, CPU 0–3 including Boost, GPU 0–2, Automatic fans; no battery or charge-limit controls |

These four profiles retain independent exact host, HID and command admission.
The three 2022 profiles use firmware modes 0 (AC Balanced), 5 (Silent), 4
(Custom) and 6 (battery Balanced). The 2021 profile admits only modes 0 and 4
on AC. All four admit CPU Low/Medium/High/Boost (0–3) independently of GPU
Low/Medium/High (0–2). They admit no Turbo or Maximum Fans command.

Charge protection on the three 2022 profiles depends on the reviewed updated
firmware. The exact-model Razer update pages and signed carrier evidence are
recorded in the [2021/2022 review](source-reviewed-blades-2021-2022.md). A fresh
charge-state GET must succeed before each SET; unsupported or failed readback
blocks the write. Firmware version strings remain provenance, not a static BIOS
allowlist. OpenBlade does not update firmware automatically.

The three 2022 profiles offer charge limits 50–80% in 5% steps or protection
off, preserving the saved threshold and protection flag independently. RZ09-0370
has no admitted charge-limit contract. Fixed fan targets and curves remain
unavailable; an existing manual fan state requires explicit, confirmed Automatic
handback before performance changes because manual target restoration is not
established. None of these profiles admits lighting, HyperBoost or driver/THX
integration. Physical validation remains pending for each model and feature.

See the [2021/2022 source review](source-reviewed-blades-2021-2022.md) and its
[admission manifest](../evidence/source-reviewed-blades/2026-11-07-models-2021-2022.json)
for exact primary sources, full hashes and per-feature decisions. Those sources
were inspected as inert data; package presence does not establish API or setup
compatibility.

## 2019/2020 exact identity and initial controls

| Laptop | Exact Windows product name | USB identity | Synapse product | Initial controls |
| --- | --- | --- | --- | --- |
| Blade 15 Base (2019), RZ09-0300 | `Blade 15 Mid 2019-Base` | `1532:0246` | 582 | Full-power AC Balanced/Gaming/Creator, Automatic fans; battery Balanced |
| Blade 15 Advanced (Early 2020), RZ09-0330 | `Blade 15 Advanced Model (Early 2020) - RZ09-033` | `1532:0253` | 595 | Full-power AC Balanced/Custom, CPU/GPU 0–2, Automatic fans; battery Balanced |
| Blade 15 Base (Late 2020), RZ09-0351 | `Blade 15 Base Model (Late 2020)` | `1532:0268` | 616 | Full-power AC Balanced/Gaming/Creator, Automatic fans; battery Balanced |

All three use query/write profile 1. Balanced is raw 0 on both AC and battery;
there is no raw-6 DC Balanced authority. The Base models use Gaming raw 1 and
Creator raw 2, without Custom or Silent. The Advanced model uses Custom raw 4,
CPU and GPU levels 0–2. CPU Boost raw 3 is observed in its source menu but remains
unavailable until an exact readable EC prerequisite can be established. The
vendor's EC update documentation does not supply that runtime read contract.

Each AC preset/level write requires a fresh valid firmware `07/8C` power response
and native Windows AC agreement. DC permits only Balanced raw 0 and Automatic
handback. Unsupported, unknown or failed power reads cannot grant writes. An
existing manual fan state needs explicit confirmed Automatic handback because
manual target restoration is unestablished. The 2019 Base's HID Gaming preset
does not grant the vendor native-library GPU overclock side effect.

Fixed fans, curves, charge limits, Maximum Fans, HyperBoost, lighting and driver
integration are not admitted. Physical validation remains pending. RZ09-0313,
RZ09-0314 and RZ09-0329 were reviewed as candidates but lack a pinned exact Windows
product name and remain excluded. No sibling or Studio identity is inferred.
See the [2019/2020 source review](source-reviewed-blades-2019-2020.md) and its
[admission manifest](../evidence/source-reviewed-blades/2026-11-07-models-2019-2020.json).

## Admission and recovery

Close Synapse before changing controls. Exact Razer manufacturer and product name
checks in both Windows registry sources, exact USB identity, responding interface,
and model-specific report geometry remain required. The reviewed vendor transport
uses 91-byte HID feature reports including the report ID and 90 protocol bytes.
Matching that geometry alone does not admit another model.

Every mutation retains fresh controller, power, session and ownership checks,
independent readback where supported, durable restoration and stop-on-ambiguity
handling. Failed or uncertain submission pauses controls; recovery reads the
current state without replaying the request. See
[recovery instructions](experimental-blade-support.md#if-controls-pause).
BIOS and EC versions are provenance unless a reviewed command-specific restriction
requires otherwise; update listings alone do not establish a thermal-control
firmware floor.

The legacy `ExperimentalBlade` internal type names, IPC flags and
`experimental-support.json` diagnostic filename are retained for compatibility.
They do not change official support classification or weaken command admission.
This change requires a coordinated full installation when deployed, including
the service and Session Agent; a tray-only replacement is insufficient.

## Controls not admitted and remaining evidence

These profiles do not initially admit fixed fans, fan curves, HyperBoost, lighting,
custom RGB frames, spatial effects, startup animation or temporary full charge.
They also do not admit Razer-driver SnapTap/function-row handling, keyboard driver
setup, native-brightness driver integration or THX integration. Reviewed vendor
package declarations do not establish a complete runtime or installation recipe.
No model inherits another laptop's key map, frame mask, driver stack or physical
validation results.

Owner reports can establish the specific applied setting, independent readback,
restoration, adapter and battery behavior, and lifecycle conditions exercised.
Record those results per model and feature. An attended sleep/resume check needs
its own authorization; support classification does not authorize development-time
hardware writes or installations. Use the
[report workflow](experimental-blade-support.md#send-a-useful-test-report) for a
bounded observation and state any remaining uncertainty.

## Pinned primary sources

The [machine-readable admission manifest](../evidence/source-reviewed-blades/2026-11-07-models.json)
records full SHA-256 pins, identity evidence, command definitions and bounded
capabilities. The shortened pins below identify the reviewed primary files;
follow the manifest for full hashes. Vendor JavaScript was inspected as inert
data; it was not executed. Firmware packages or matching download names alone
were not used as proof of command semantics.

| Product | Source role | Exact primary source | SHA-256 prefix |
| --- | --- | --- | --- |
| 694 | Model and feature policy | [main.1b1b68e07824e5af9833.js](https://apps.razer.com/synapse/products/694/mw/main.1b1b68e07824e5af9833.js) | `F1AEEAFF4BBDB631` |
| 694 | Base command and transport | [rzDevice25.06135650b7453937e0e8.js](https://apps.razer.com/synapse/products/694/mw/rzDevice25.06135650b7453937e0e8.js) | `0E5BD5C21DD610E5` |
| 694 | Thermal policy | [6737.63910b6756fd6d249dc6.js](https://apps.razer.com/synapse/products/694/mw/6737.63910b6756fd6d249dc6.js) | `488D72368F0EBFB2` |
| 694 | Battery UI | [ui-main.415cc878.js](https://apps.razer.com/synapse/products/694/ui/static/js/main.415cc878.js) | `C22750AAD6E54EE8` |
| 695 | Model and feature policy | [main.27df2e7ddab0b6bc34cc.js](https://apps.razer.com/synapse/products/695/mw/main.27df2e7ddab0b6bc34cc.js) | `A12CD7047CC1CF09` |
| 695 | Base command and transport | [rzDevice25.60bfcef58690c8bb715d.js](https://apps.razer.com/synapse/products/695/mw/rzDevice25.60bfcef58690c8bb715d.js) | `2D69449422DAC50C` |
| 695 | Thermal policy | [6737.7ec91e479ef65c194b6c.js](https://apps.razer.com/synapse/products/695/mw/6737.7ec91e479ef65c194b6c.js) | `22D359EB7EDA08A9` |
| 695 | Battery UI | [ui-main.65ed4ee5.js](https://apps.razer.com/synapse/products/695/ui/static/js/main.65ed4ee5.js) | `74ECA71816B57B4C` |
| 696 | Model and feature policy | [main.ded18de8aee09a8f8238.js](https://apps.razer.com/synapse/products/696/mw/main.ded18de8aee09a8f8238.js) | `1A9CC3E1E8E28BB8` |
| 696 | Base command and transport | [rzDevice25.6339454cb5b2fe587300.js](https://apps.razer.com/synapse/products/696/mw/rzDevice25.6339454cb5b2fe587300.js) | `8780AA380D11665A` |
| 696 | Thermal policy | [6737.2463f7361e4d145671a2.js](https://apps.razer.com/synapse/products/696/mw/6737.2463f7361e4d145671a2.js) | `4EF6AFB6402DD50C` |
| 696 | Battery UI | [ui-main.cb24c0fc.js](https://apps.razer.com/synapse/products/696/ui/static/js/main.cb24c0fc.js) | `C882F2130FC41DC7` |
| 711 | Model and feature policy | [main.1f8e0c604f19af3ea24c.js](https://apps.razer.com/synapse/products/711/mw/main.1f8e0c604f19af3ea24c.js) | `C8C0C794D6D01867` |
| 711 | Base command and transport | [rzDevice25.c8da1ead63e6cf002d32.js](https://apps.razer.com/synapse/products/711/mw/rzDevice25.c8da1ead63e6cf002d32.js) | `02C55B78B3EABDCC` |
| 711 | Thermal policy | [6737.c63928b0e4fd1b7673b4.js](https://apps.razer.com/synapse/products/711/mw/6737.c63928b0e4fd1b7673b4.js) | `3EF16C0BEB5E8399` |
| 711 | Battery UI | [ui-892.12b29c32.chunk.js](https://apps.razer.com/synapse/products/711/ui/static/js/892.12b29c32.chunk.js) | `FE1298AD9FAAE663` |
