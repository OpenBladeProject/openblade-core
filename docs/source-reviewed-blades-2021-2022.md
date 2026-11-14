# Blade 2021/2022 source review

The later [performance and fan review](source-reviewed-thermal-967.md) expands manual fan admission and adds exact presets. Fan exclusions below describe the original admission.

The later [lighting review](source-reviewed-lighting.md) adds bounded lighting controls to these profiles. Lighting exclusions below describe the original admission.

This review covers four exact Blade profiles. Pinned Synapse middleware and UI
sources establish bounded thermal controls. The three 2022 profiles also have a
reviewed battery charge-limiter contract. Physical validation is pending for
every model and feature in this batch.

| Laptop | Exact Windows product name | USB | Synapse product | Initial controls |
| --- | --- | --- | --- | --- |
| Blade 14 (2021), RZ09-0370 | `Blade 14 - RZ09-0370` | `1532:0270` | 624 | Balanced and Custom on full-power AC; CPU 0–3, GPU 0–2; Automatic fans |
| Blade 14 (2022), RZ09-0427 | `Blade 14 (2022) - RZ09-0427` | `1532:028C` | 652 | AC/battery Balanced, AC Silent and Custom; CPU 0–3, GPU 0–2; Automatic fans; charge protection |
| Blade 15 (2022), RZ09-0421 | `Blade 15 (2022) - RZ09-0421` | `1532:028A` | 650 | AC/battery Balanced, AC Silent and Custom; CPU 0–3, GPU 0–2; Automatic fans; charge protection |
| Blade 17 (2022), RZ09-0423 | `Blade 17 (2022) - RZ09-0423` | `1532:028B` | 651 | AC/battery Balanced, AC Silent and Custom; CPU 0–3, GPU 0–2; Automatic fans; charge protection |

RZ09-0370 is the NVIDIA GPU model in product 624's guide. The guide's separate
AMD GPU model, RZ09-0391, is outside this admission. Exact Windows names come
from primary DMI records pinned to an immutable repository revision.

## Per-model command decisions

Balanced is firmware mode 0, Silent 5 and Custom 4. The 2022 models explicitly
select battery Balanced mode 6. RZ09-0370 does not select that DC contract;
its initial runtime admission is limited to full-power AC even though the vendor
shows Balanced in its battery UI. No profile in this batch admits Turbo or
Maximum Fans.

Custom CPU levels are Low 0, Medium 1, High 2 and Boost 3. GPU levels are Low 0,
Medium 1 and High 2. Each model's actual UI caller enables the CPU Boost selector.
The direct thermal setters use class/profile 1, CPU zone 1 and GPU zone 2. This
does not admit the separate DC CPU Boost power-bit toggle, DLL overclocking APIs
or HyperBoost. The 2021 profile selects `rzDevice25LedMatrixBladeWattage`; its
reviewed HID thermal commands are inherited from its separately pinned base
device.

The transport uses report ID 0, interface 2, a 90-byte protocol report within
a 91-byte native HID feature report, and transactions 1–31. Ordinary thermal
requests use `0D/82` and `0D/02`; advanced CPU/GPU levels use `0D/87` and `0D/07`.
Matching transport geometry does not admit a different laptop.

Only Automatic fan handback is admitted. Vendor fixed fan ranges and dynamic
range replacement are observed source metadata. Fixed targets, curves and their
restoration are not admitted for these profiles. An existing manual fan state
blocks performance changes until explicit Automatic handback is confirmed.
Handback retains the current preset rather than forcing Balanced.

## Charge protection and firmware readiness

RZ09-0427, RZ09-0421 and RZ09-0423 offer 50, 55, 60, 65, 70, 75 and 80 percent.
The app's 100% selection disables protection. The flag and saved threshold remain
separate: disabling protection preserves the actual threshold. RZ09-0370 has no
admitted limiter contract.

Use exact-model firmware that provides Battery Health Optimizer before enabling
these 2022 controls. The signed carriers below establish reviewed provenance;
their versions and image hashes are not static runtime allowlists. A fresh,
independent `07/92` GET must establish a valid limiter state before each write.
The one-byte `07/12` SET packs the enable flag in bit 7 and threshold in bits 0–6.
A successful ACK may be empty or contain one returned-state byte; it need not
echo the request. Only an independent GET of the exact requested raw value
confirms the setting. If GET still returns the exact pre-write byte, OpenBlade
allows up to nine reads, 250 ms apart, without repeating the SET. A different
value, failed or invalid read, cancellation or exhausted confirmation window
pauses writes and reconciles read-only. This is software confirmation tolerance;
physical firmware latency has not been established on these models.

| Model | Official package reviewed | Provenance and scope |
| --- | --- | --- |
| RZ09-0370 | [BIOS 1.08_v3](https://mysupport.razer.com/app/answers/detail/a_id/5502/) | BIOS provenance; no battery limiter authority |
| RZ09-0427 | [March 30, 2023 firmware](https://mysupport.razer.com/app/answers/detail/a_id/9724/) | BIOS 3.06, EC 1.06, MCU 1.01.00, PD 11; Battery Health Optimizer and USB4 |
| RZ09-0427 | [Older EC 1.03](https://mysupport.razer.com/app/answers/detail/a_id/14993/) | June 8, 2022; separate from the later Battery Health Optimizer package |
| RZ09-0421 | [BIOS 2.06](https://mysupport.razer.com/app/answers/detail/a_id/9729/) and [EC/keyboard update](https://mysupport.razer.com/app/answers/detail/a_id/9727/) | EC 1.03, MCU 1.03.00; January 27, 2025 Battery Health Optimizer update |
| RZ09-0423 | [BIOS 2.01](https://mysupport.razer.com/app/answers/detail/a_id/6197/) and [firmware update](https://mysupport.razer.com/app/answers/detail/a_id/14775/) | EC 1.05, MCU 1.02.00 and variant-specific VBIOS; January 27, 2025 |
| RZ09-0421 | [Optional Thunderbolt NVM update](https://mysupport.razer.com/app/answers/detail/a_id/14711/) | October 18, 2024 Thunderbolt 5 accessory compatibility; no thermal or charge prerequisite |

The optional RZ09-0421 NVM updater labels its version 43.03 in a generic
`BIOSVER` INI field, but its inert script selects an NVM payload. It is not
system BIOS 43.03; the separately reviewed system BIOS is 2.06. Updater target
SSID/SKU fields describe package applicability, not OpenBlade host admission.
Firmware images, INI entries and digital signatures do not establish HID command
semantics or physical results.

## Evidence and remaining validation

The [admission manifest](../evidence/source-reviewed-blades/2026-11-07-models-2021-2022.json)
records full-source URLs, SHA-256 hashes, byte sizes, short UTF-8 byte-offset
quotes, immutable primary DMI pins, and signed-carrier and extracted-image hashes.
The reviewed runtime catalog contains 15 profiles: 13 source-reviewed and two
physically validated. This batch adds four source-reviewed profiles. Counts do
not transfer per-feature evidence between models.

The manifest contains sanitized metadata rather than firmware archives, driver
packages, complete vendor modules, full DMI dumps or installation recipes. No
collected updater or vendor script was executed. No driver/device change or
hardware test was performed. Resource declarations remain subject to the
separate driver catalog applicability review; package presence grants no
runtime or installation authority.

Remaining physical evidence concerns successful setting changes, independent
readback, restoration, power transitions and lifecycle behavior on each model.
Lighting, manual fans, Maximum Fans, HyperBoost and driver/THX integrations remain
outside this admission. Ordinary thermal sources establish no general firmware
version floor. See the [support guide](source-reviewed-blade-models.md) for
admission, recovery and diagnostics.
