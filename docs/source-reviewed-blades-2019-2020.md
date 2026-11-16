# Source-reviewed Blade 15 models from 2019 and 2020

The later [performance and fan review](source-reviewed-thermal-967.md) expands manual fan admission and adds exact presets. Fan exclusions below describe the original admission.

The later [lighting review](source-reviewed-lighting.md) adds bounded lighting controls to these profiles. Lighting exclusions below describe the original admission.

This review adds three exact profiles to official source-reviewed support. Physical validation remains pending. The [admission manifest](../evidence/source-reviewed-blades/2026-11-07-models-2019-2020.json) records 79 complete source pins, 97 byte-offset excerpts, exact identity evidence and two signed firmware carriers.

## Exact identities and admitted controls

| Model | USB identity | Exact Windows product | Admitted thermal controls |
| --- | --- | --- | --- |
| Blade 15 Base (2019), RZ09-0300 | 1532:0246, Synapse 582 | `Blade 15 Mid 2019-Base` | Balanced, Gaming, Creator; Automatic fan handback |
| Blade 15 Advanced (2020), RZ09-0330 | 1532:0253, Synapse 595 | `Blade 15 Advanced Model (Early 2020) - RZ09-033` | Balanced, Custom CPU/GPU levels 0-2; Automatic fan handback |
| Blade 15 Base (Late 2020), RZ09-0351 | 1532:0268, Synapse 616 | `Blade 15 Base Model (Late 2020)` | Balanced, Gaming, Creator; Automatic fan handback |

The product names above are actual SMBIOS text from pinned primary reports, not names inferred from report folders. They are coupled to each exact Razer VID/PID and the paired Windows registry identity sources. A generic or truncated product string does not admit another device. SKU values remain diagnostic. Razer's exact Japanese guides identify [RZ09-0300](https://dl.razerzone.com/master-guides/RazerSynapse3/Blade-00000582-ja.pdf), [RZ09-0330](https://dl.razerzone.com/master-guides/RazerSynapse3/BLADE15ADVANCED-00000595-ja.pdf) and [RZ09-0351](https://dl.razerzone.com/master-guides/RazerSynapse3/Blade-00000616-ja.pdf) on page 3.

All three use interface 2, report ID 0, a 90-byte protocol body in a 91-byte native feature report, cycling transaction IDs 1-31, and thermal query/write selector 1. Balanced is raw 0 on AC and battery. The Base models' Gaming and Creator values are 1 and 2; they are distinct typed presets throughout service IPC, stored profiles, notifications, shortcuts and Windows power-mode preferences. They are not Turbo or Maximum Power aliases. The Advanced model's Custom value is 4, with CPU/GPU levels 0-2.

Preset changes require full-power AC. Each write requires a fresh successful typed `07/8C` read of actual and recommended adapter wattage, matching Windows power evidence, exact device/controller/session admission and independent readback. Unknown, malformed, unavailable, limited or contradictory power results block the write. Synapse's fallback after a failed adapter query is not admitted. Automatic handback preserves the current exact preset and selector, including Gaming raw 1 and Creator raw 2; on battery it accepts only the reviewed Balanced raw 0 baseline and matched power evidence.

Manual fan targets, curves, Silent, Turbo, Maximum Fans, charge protection, HyperBoost, lighting and driver/THX runtime or setup integrations are not admitted by these profiles. An unrepresentable manual baseline blocks preset changes until an explicit verified Automatic handback. Vendor Gaming and Custom code can additionally call native `SetGPUValue`; these profiles control the typed thermal HID preset only and do not reproduce or claim native GPU overclocking.

## Firmware provenance and restrictions

RZ09-0330 BIOS 1.06 and EC 01.01 carriers were downloaded from Razer, verified as Authenticode-valid Razer USA files, and statically extracted. Their metadata declares RZ09-0330 and SSIDs 1A582009/1A58200A. The committed manifest contains carrier, INI and image hashes; no updater, driver or script was executed, and no proprietary image is committed.

Razer's [CPU Boost article](https://mysupport.razer.com/app/answers/detail/a_id/2832) and [EC update article](https://mysupport.razer.com/app/answers/detail/a_id/2827) establish a firmware prerequisite for RZ09-0330 Boost. The EC article names narrower Advanced and Studio variants. OpenBlade cannot currently verify that exact prerequisite through an admitted typed EC query, so CPU level 3 remains unavailable. Ordinary CPU levels 0-2 do not borrow that authority, and firmware artifact metadata is not a general runtime version allowlist.

The published RZ09-03009 BIOS 1.03 guide links `https://rzr.to/CaQyK`; on the review date it redirected to Razer's 404 page. No carrier was available for static inspection from that link. The guide is specific to the GTX1660 variant and must not be generalized to RZ09-03006. No exact public RZ09-0351 BIOS/EC carrier was found in the reviewed listings; Intel ME updates do not establish a BIOS/EC artifact. These availability gaps are recorded separately from the exact-model command evidence.

## Reviewed candidates still pending admission

RZ09-0313/024B (2019) and RZ09-0329/0256 (2020) have useful command-source evidence but lack a confirmed exact Windows product identity in this review. They remain outside the supported catalog. RZ09-0314/024C was also pending here; the later [issue 949 review](source-reviewed-blades-issue-949.md) establishes its exact identity and admits it separately. Product 587's middleware support link points to the different RZ09-0301 page; its exact guide and UI support link identify RZ09-0313. That stale navigation link does not authorize using RZ09-0301 identity evidence.

## Verification boundary

Software regressions cover exact identity rejection, every Base preset transition, current-preset Automatic handback, unavailable or malformed adapter reads, power and identity changes at submission, unsupported commands, independent readback and stop-on-ambiguity recovery without replay. Each new model is reviewed against every driver-catalog artifact, with no inherited driver or audio authority. Physical fan behavior, thermal results and restoration remain pending per model.
