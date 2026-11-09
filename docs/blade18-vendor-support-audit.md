# Blade 18 vendor-source support audit

Tracking: [#739](https://github.com/OpenBladeProject/openblade-core/issues/739).
Reviewed on 2026-11-05 for RZ09-0484 / USB `1532:02A0` / Synapse product 672.
This follow-up uses vendor files as data and the existing owner evidence. It
adds source-backed host input handling, without a new firmware command or a
physical-validation claim.

## Current Synapse sources

The current [product manifest](https://apps.razer.com/synapse/products/672/mw/manifest.json)
reports build `2610250617`, commit `32d6d47e27cd380c191f721285e435910a890675`.
The current index loads the entry below. A new bundle name is not itself a
change to the exact model's command contract.

| Artifact under product `672/mw/` | Bytes | SHA-256 |
| --- | --- | --- |
| [manifest.json](https://apps.razer.com/synapse/products/672/mw/manifest.json) | 9041 | `029B4CC413AAECEEA843F703B7255A60475517AAD06324464AA2F724A3121318` |
| [main.5cd0d12a2bff4b5edcf2.js](https://apps.razer.com/synapse/products/672/mw/main.5cd0d12a2bff4b5edcf2.js) | 169941 | `61832E9F7C55E727D929DE103136D0684919020D7BA833FCDF16C9061667DAA4` |
| [6737.e50bb836b878190315d7.js](https://apps.razer.com/synapse/products/672/mw/6737.e50bb836b878190315d7.js) | 1957301 | `651F63DCC0EFC92B0FD7496EE70751707B5F650F20AF5C01C3255B61817C1997` |
| [rzDevice25.85ce8530211c29f3c304.js](https://apps.razer.com/synapse/products/672/mw/rzDevice25.85ce8530211c29f3c304.js) | 272259 | `DFB3E3612D36B20910AF02E61A17ED64A1FFD3781E09C63BAAEE7CBEFDEBCA24` |

Static comparison with the [October source review](blade18-source-feature-recovery.md)
found identical product-descriptor and runtime-data definitions. The current
entry's descriptor begins at character offset 145627 and runtime-data seed at
162447. The shared middleware and command-class files still match the reviewed
hashes. The installer index also retains its reviewed hash and resource version
1.0.1. The current entry does not add an exact-product HyperBoost,
Custom-manual-fan, firmware Fn-primary, or system-monitoring feature contract.
Existing command definitions, ranges and restoration rules remain authoritative.

## Official firmware packages

Both original carriers passed Windows Authenticode verification with signer
`Razer USA Ltd.`. Their NSIS archives were extracted with 7-Zip 26.05; none of
the extracted updaters, flash utilities, DLLs, drivers or scripts was executed.
Vendor bytes remain outside the repository.

| Official carrier | Bytes | SHA-256 |
| --- | --- | --- |
| [BIOS 2.09](https://dl.razerzone.com/drivers/RazerBlade18RZ09-0484/KI890-BIOSv0209.exe) | 8054624 | `EA6B01417B8B868A60C7F85DC194AAA8E8EC0D117DEDAA6F3689FF09FBDE9741` |
| [EC 1.07 / keyboard 1.04 / touchpad / VBIOS](https://dl.razerzone.com/drivers/RazerBlade18RZ09-0484/RazerUpdater_v1.1.1.3_KI890_EC_0107_MCU_010400_TP_VBIOS_v5%201.exe) | 16927128 | `6E383E9EA40AC5D45BE0DBD38CBA9C8F92368317D4AD06AD8DA1D411E7EDB2D7` |

The BIOS carrier's `torden.ini` identifies RZ09-0484, board `1A583007` and
BIOS 2.09. The combined carrier lists that board's R/S/T/U/0 SKU families,
EC 01.07 and MCU 01.04.00, with separate GPU VBIOS selections. These are updater
applicability and payload provenance, not new OpenBlade SKU or firmware gates.
The existing owner evidence already reports BIOS 2.09; this audit does not
establish an update as a remedy for the reported failures.

| Extracted payload | Bytes | SHA-256 |
| --- | --- | --- |
| `KI890_209_BIOSME.bin` | 28377088 | `9D7A6EF09C878A86F77E3FFFD49505FDEFD552B1F43C6E65D483B1D90B06EF29` |
| `KI890_EC_01_07.bin` | 262144 | `A1D2E6EBE7DE811661D0D61FD9E4AE108F8103FD8D65B1AF72996FAA1BEADDE2` |
| `KI890_Keyboard_FW_v1.04.00_20230412.hex` | 166863 | `2A89C1FF6B856551328AFE667BB81B31D55722F83618D27311D5B070F2962C73` |

UEFIExtract NE A76 parsed the BIOS image using its flat `unpack` mode. The
initial firmware inspection did not recover a usable Windows CPU-temperature
mapping or an additional exact-model control contract. This is an investigation
limit, not evidence that the firmware lacks those functions. A firmware image,
flashing command or embedded driver alone does not supply the complete runtime
HID query/set/readback contract needed to admit a new OpenBlade control.

## Support status and remaining evidence

| Area | Current result | Remaining evidence |
| --- | --- | --- |
| Detection and controls reported in #739 | Existing fixes cover optional-query startup isolation, setter acknowledgements and completed unconfirmed reads. This follow-up fixes same-preset requests rejecting fixed fans. | The issue has no owner export after the nightly .97 follow-up. Physical recovery remains unconfirmed. |
| Performance, fixed fans and lighting | Existing exact-model command definitions remain supported experimentally. Balanced and Silent have their own fixed-fan ranges. | Confirm actual application and restoration on the owner's laptop where not already observed. |
| CPU temperature and fan curves | The shared Windows TZRZ reader already includes Blade 18. Missing temperature holds the Balanced curve at its admitted maximum. | A measured standalone CPU source or exact-model Windows counter mapping; another recording of fan commands does not establish one. |
| Fn shortcuts and primary behavior | The driver backend now combines the reviewed per-device API with Blade 18's own Fn-state decoder for primary function-row handling. Windows-hook shortcuts retain their existing scope. | Physical input behavior and game compatibility remain unverified; backend switching must preserve owned-hook and output recovery. |
| HyperBoost and Custom/Performance manual fans | The current vendor files add no enabling HyperBoost contract or admitted manual-fan contract for these modes. | Complete exact-model command, range, readback and restoration evidence before implementation. |
| Legacy THX | Existing-stack runtime and fresh installation remain separate contracts. | The unresolved endpoint FX binding is described in [legacy THX](thx-legacy-interface.md); unrelated HID recordings do not establish it. |

The same-preset fix keeps fresh admission and the confirmed baseline, then
returns without a setter when the requested preset is already active. Tests
cover Blade 18 Balanced/Fixed and Silent/Fixed and the shared 029F/029D paths.
They require unchanged fan mode and targets, successful baseline queries and
zero setters. Different-preset transitions retain their existing admission.

Future owner follow-up should target a named unresolved result. The current
request in #739 is one ordinary OpenBlade diagnostic export with settings left
unchanged and Synapse fully exited, rather than another broad Observer matrix.
