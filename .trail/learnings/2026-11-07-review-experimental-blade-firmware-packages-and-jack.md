---
title: Review experimental Blade firmware packages and variant provenance
author: Jack
date: 2026-11-07
touches:
  - artifacts/experimental-firmware-review/firmware-inventory.json
---

## Decided

On 2026-11-06, the five experimental Blade families' official firmware packages were acquired as data and all 13 outer packages passed Windows Authenticode as Razer USA Ltd. SHA-256, official article/short/CDN URLs, static updater cases and extracted payload hashes are in the task-local artifacts/experimental-firmware-review/firmware-inventory.json. Vendor bytes remain ignored, local artifacts. No updater, flash script or extracted code was executed, and no product support status changed. Package provenance and declared update targets do not establish OpenBlade physical application, independent readback, restoration, or lifecycle behavior.

RZ09-0483 standard BIOS 2.05 / EC 2.01 use SSID 1A583006; Lamborghini BIOS 1.06 / EC 1.01 use SSID 1A583009. Keep their firmware bytes separate. The Lamborghini article's shared MCU 1.06 link resolves to a configuration naming only the standard SSID, so Lamborghini applicability remains unresolved by this review. RZ09-0484 BIOS 2.09 and the EC 1.07 / MCU 1.04 combined updater are distinct; the latter also contains touchpad and GPU-specific vBIOS updates. RZ09-0530 uses separate BIOS 1.06 / EC 1.03 / MCU 1.04 wrappers. RZ09-0328 BIOS 1.06's article names -03286/-03287/-03289. No standalone EC package was listed for RZ09-0328 or RZ09-0482 in the reviewed model pages; do not infer BIOS-image EC contents from empty updater EC fields.

## Tried and ruled out

RZ09-0482 BIOS is linked by live catalogue article 13732 to article 13697, which links https://rzr.to/tT4YXR; no guessed article or CDN filename is needed. Its image is PI490_200.rom. Razer's initial pre_page_check can be resolved by retaining cookies and repeating the original official GET once, as the existing firmware client does. Browser search output alone missed this link. Native NSIS packages contain no ordinary 7z/ZIP/CAB magic and Windows tar did not open them; official portable 7-Zip 26.05 statically listed and extracted the firmware/configuration without running the carrier.

## Watch out

Firmware package selection is more specific than runtime laptop-family admission. Updater SSID/SKU case distinctions are provenance for these downloaded installers, not authority to add SKU gates to OpenBlade host admission or to expose another command. Standard EC and MCU wrappers can contain both images while their torden.ini selects only one update action. Existing upstream owner tests and Blade 18 Synapse Observer actions remain useful partial evidence, but must not be silently relabeled complete OpenBlade physical validation. The user was asked whether official support should mean maintained source-reviewed support with physical validation tracked separately; no answer had arrived when this note was written.
