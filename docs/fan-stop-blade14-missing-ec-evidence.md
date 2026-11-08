# Blade 14 EC evidence gaps: RZ09-0370, RZ09-0482 and RZ09-0508

Reviewed 2026-11-08 against core
`c46ee058eff2a40b7ddbd82af3dac48c397d80b3`. **No exact laptop EC payload or
complete alternative Stop/Auto contract was identified for these three models.**
This review expands the package and embedded-image investigation; it does not
admit a firmware command, select an EC version, or change runtime support.
Physical validation is a separate status and was not attempted.

All vendor executables, libraries, drivers and scripts were treated as inert
data. No updater, vendor assembly, driver, ACPI method or hardware interface was
executed. Only offline archive and firmware parsers ran. Downloads and analysis
outputs remain ignored under `artifacts/model-expansion/remaining-blade14/`;
vendor bytes must not be added to Git or redistributed with OpenBlade.

## Exact official inputs

The official [firmware index](https://mysupport.razer.com/app/answers/detail/a_id/4166/)
lists BIOS updates for RZ09-0370 and RZ09-0508, and BIOS plus keyboard firmware
for RZ09-0482. The model-specific sources are
[0370 BIOS 1.08_v3](https://mysupport.razer.com/app/answers/detail/a_id/5502/),
[0482 firmware](https://mysupport.razer.com/app/answers/detail/a_id/13697/),
[0482 keyboard firmware](https://mysupport.razer.com/app/answers/detail/a_id/13877/)
and [0508 BIOS](https://mysupport.razer.com/app/answers/detail/a_id/14881/).
No separate exact-model EC package was identified in those reviewed public
sources. This is a bounded search result, not proof that Razer has no such image.

Direct article fetches encountered Razer's pre-page redirect; cached official
search results supplied the article/index descriptions. All four direct
`dl.razerzone.com` carrier downloads succeeded. Thus the payload gap below must
not be described as a failed firmware download.

| Exact input | Bytes | SHA-256 |
|---|---:|---|
| [PI410_PI411-BIOSv0108_v3.exe](https://dl.razerzone.com/drivers/Razer%20Blade%2014%22%20%282021%29%20%7C%20RZ09-0370/PI410_PI411-BIOSv0108_v3.exe) | 5,152,624 | `4477f0c16b6bf98cbc81152a90e734ae3a1b250b46a98422aae6aeb09c236bfa` |
| [PI490 BIOS 2.00 carrier](https://dl.razerzone.com/drivers/Razer%20Blade%2014%E2%80%9D%20%282023%29%20%7C%20RZ09-0482x/RazerUpdater_v1.1.1.3_PI490-BIOSv02.00.exe) | 5,830,056 | `83d3b1c29573f3e0e8f20a8ba1700a43c20ec3590604f162a1d201f4481abd13` |
| [PI4100 BIOS 1.06 carrier](https://dl.razerzone.com/drivers/Razer%20Blade%2014%E2%80%9D%20%282024%29%20%7C%20RZ09-0508/RazerUpdater_v1.1.1.3_PI4100-BIOSv01.06.exe) | 5,852,096 | `ec121d3fd773d3d48ca5a6b145315269d21d1ee2fa5114c3eb79fa9538a824b4` |
| [PI490 keyboard MCU 1.04.00 carrier](https://dl.razerzone.com/drivers/Razer%20Blade%2014%E2%80%9D%20%282023%29%20%7C%20RZ09-0482x/RazerUpdater_v1.1.1.3_PI490-MCUv010400v2.exe) | 1,330,264 | `9653d899701a889f3e533e8e4a6733869ca3b4bc5c66942b6512d3d848c40e51` |

All four carriers had **Valid Authenticode signatures for Razer USA Ltd.** The
0370 carrier signer thumbprint is
`3FDDBD6F820144422494342EF44898D732E068DC`; the three 2023/2024 carriers use
`39B818FD58E5287EAB2F9371F7E6A6B8B1A0E8D8`. Carrier hashes match the repository's
existing reviewed firmware catalogs. Signature trust establishes provenance,
not EC semantics or authorization to run an updater.

## Outer packages and exact-model binding

Complete bounded NSIS parsing recovered 15 blocks from each BIOS carrier and
13 from the keyboard carrier: 58 blocks with zero parsing/decompression errors.
Each count includes the NSIS header block. Named 7-Zip extraction is useful for
reading configuration, but each BIOS archive has two entries named
`devcon.exe`; `-aos` skips the second. The separate all-block manifest includes
both, so that filename collision does not leave an unexamined member.

All three BIOS configurations leave `ECVER` and `ECExec` empty. Their selected
`BIOSExec` is `FlashWin.bat`; no standalone EC member or separate EC-flash
selection was identified in these packages.

| Model | Packaged selection | Selected image and invocation, read as text |
|---|---|---|
| RZ09-0370 | Explicit SKU `RZ09-0370`; SSIDs `1A582013` and `1A582014`; BIOS `1.08` | `P1108.rom`; `AFUWINx64.EXE P1108.rom /p /b /n /k /RLC:E` |
| RZ09-0482 | SSID `1A582020`; SKU wildcard; BIOS `2.00` | `PI490_200.rom`; `AFUWINx64.EXE PI490_200.rom /p /b /n /k /capsule /q` |
| RZ09-0508 | SSID `1A582022`; SKU wildcard; BIOS `1.06` | `PI4100_106.rom`; `AFUWINx64.EXE PI4100_106.rom /p /b /n /k /capsule /q` |

The official model-specific download paths/articles and these packaged board
identifiers provide package binding. Wildcard SKU does not establish
applicability to another model. AFU switches alone also do not establish an
embedded EC image, its ISA, or a Stop command.

| Extracted image | Bytes | SHA-256 |
|---|---:|---|
| `P1108.rom` | 16,777,216 | `c39d1df88dca0225a7107bb576d6303fbc1c8993ad674f9f811c40d4df0939cc` |
| `PI490_200.rom` | 33,554,432 | `9e832d5bc12ffc15a7ff00188d7b1afabc053e1482e412e427a7aaa3b3ed3eb8` |
| `PI4100_106.rom` | 33,554,432 | `3362ff0f4c4d3010cb0a655e4ce3ae72f273833019462e3ec8a5dbd4baf9d815` |

## Embedded BIOS investigation

UEFIExtract A76 parsed the three full ROMs, including compressed UEFI sections.
Its default hierarchical dump initially hit Windows path-length errors; those
partial outputs were not treated as complete. Fresh short-basename inputs
(`37.rom`, `48.rom`, `50.rom`) with the flat `unpack` mode completed with exit
code zero. Each retained the parser warning that the last VTF appears inside a
compressed item. Complete flat-output inventories record every binary body:

| Model | UEFI binary bodies | Unique body hashes | PSP/BIOS directories | Referenced entries | Unique PSP entries |
|---|---:|---:|---:|---:|---:|
| RZ09-0370 | 3,235 | 1,547 | 4 | 65 | 61 |
| RZ09-0482 | 4,109 | 1,583 | 6 | 166 | 154 |
| RZ09-0508 | 4,167 | 1,611 | 6 | 166 | 154 |

A76 disables its experimental AMD-specific image parser. Its non-empty
"Padding" classification therefore did not establish that those areas were
irrelevant. A separately pinned PSPTool source checkout parsed the AMD PSP and
BIOS directories and extracted/decompressed their referenced entries. All
enumerated types were recognized by that parser; no encrypted entry, extraction
exception or decompression failure was recorded. The entries identify AMD
boot/security, SMU, PMU, graphics, USB, BIOS and related components; none was
identified as the laptop EC image.

The UEFI body search included ASCII and both alignments of UTF-16-derived text
for exact board identifiers and EC/firmware/KBC/fan labels. BIOS bodies include
`ReFlash` with the string `EC FIRMWARE Update`, `AmdCpmEcInitDxe` and
`AmdCpmEcInitPeim`. Those are BIOS host modules or labels; a string or module
name is not a standalone EC payload or complete fan command implementation.
The 128-KiB `AMI ROM hole` body begins with a firmware-volume header (`_FVH`
at offset `28`), rather than establishing a laptop EC image by its size.

No EC version, ISA, mapped register descriptor banks, mailbox completion
handler, direct `FE` zero-duty branch, all-channel observation or Automatic
restoration implementation was established for these exact models. In
particular, neither image resemblance nor another PI-series model's RV32 map
was used as proof. The parsers' recognized structures and label searches do
not rule out every unreferenced area, proprietary embedding or unknown format.
The conclusion remains **no EC payload identified**, not universal absence.

## RZ09-0482 keyboard MCU is a separate component

The keyboard carrier's `torden.ini` explicitly selects SKU `RZ09-0482`, SSID
`1A582020`, `MCUVER=01.04.00` and `MCUExec=McuUpdate.bat`, with EC fields empty.
The inert batch selects:

```text
RazerUpdaterForBlade.exe update 1532 029D PI490_keyboard_FW_v1.04.00_20230608.hex
```

The 166,752-byte HEX member has SHA-256
`42adb76bcb84203237284ed652955e6cdc1ebfb86de4fedb4b780cdf364be54c`.
Strict Intel HEX decoding validated checksums, records, address bounds,
non-overlap and EOF, producing 59,456 bytes at base zero with SHA-256
`dc4d17d4e552922c7712225301bd31474d61e4b12908e00ffa02da4e2fccbe05`.
Its initial vector words resemble an ARM/Thumb MCU image; this does not
establish the laptop EC's ISA or version. This investigation did not establish
a complete zero-duty and Auto-restoration contract from the keyboard MCU.
Do not substitute keyboard MCU `1.04.00` for an EC-version admission rule.

## Reproduction and remaining source leads

The artifact directory contains `carrier-pins.json`, `nsis-manifest.json`,
`named-member-manifest.json`, `analysis-tool-pins.json`, per-image
`*-uefi-inventory.json`, `*-psp.json` and `*-psp-inventory.json`, plus extraction
logs, parser wrappers and bounded label-search results. `inventory_packages.py`
uses the repository's strict NSIS parser with a local-process 64-MiB decoded
member limit for these 32-MiB BIOS members; shipped parser files and limits
remain unchanged.

The independent analysis tools are pinned to their actual downloaded bytes:

| Tool/source | Bytes | SHA-256 |
|---|---:|---|
| [UEFIExtract A76 win64 ZIP](https://github.com/LongSoft/UEFITool/releases/download/A76/UEFIExtract_NE_A76_win64.zip) | 497,165 | `0e723a7c57526e1d872d8caa3ff33f1220b1102844d19ec75ebbfb30fb351621` |
| Extracted `UEFIExtract.exe` | 1,119,744 | `30d063cf42f4798316b0817fad81cf063aa9f35d6dfae66b9665429ca8c07d87` |
| [PSPTool source at `50554038893026b1c9c7a1b19199a800f1e63730`](https://github.com/PSPReverse/PSPTool/archive/50554038893026b1c9c7a1b19199a800f1e63730.zip) | 93,060 | `fb302d79d7c9b7e3ebb96b86e1e4cdbd195c074c0231bea4512e65f6fa893c29` |
| Existing 7-Zip 26.02 `7z.exe` | 576,000 | `83967f1b02b43c4efeda302795722c809e0e81b8307de73558d10484d5676a7d` |
| Reused `blade_firmware/formats.py` | 6,823 | `1bb3c7a20e67f6c238de851e63bf49d46041684c2ee35bcc8387391f62f82d7d` |

PSPTool used isolated `prettytable 3.16.0` and `wcwidth 0.2.13` under this
artifact directory, with the existing Python runtime's cryptography library.
It was not installed into the shared runtime and did not decrypt any entry.

The next useful source request is specific for each model:

- **0370:** an exact PI410/PI411 laptop EC update/service image and matching
  release/flasher metadata covering SSIDs `2013` and `2014`. BIOS 1.08_v3 is
  available and investigated; another copy of that carrier does not close the gap.
- **0482:** the exact PI490 laptop EC image or complete vendor thermal control
  implementation bound to SSID `2020`, with a reliable EC-version identity.
  The public keyboard MCU package is available and separately identified.
- **0508:** an exact PI4100 laptop EC package or complete vendor thermal command
  implementation bound to SSID `2022`. The public BIOS 1.06 image is available
  and investigated; no EC version is selected from its BIOS version.

For any supplied source, review ingress/completion, independent actual-PWM and
tach observation, every configured fan channel, zero-duty handling and exact
Automatic restoration before proposing a profile. Existing two-fan contracts
provide software reuse only after that model's own evidence is complete.
No further hardware capture or physical test was requested by this review.
