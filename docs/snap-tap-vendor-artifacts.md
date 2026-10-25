# SnapTap exact-model vendor artifact review

Reviewed 2026-10-20. This was a static-only review: the vendor installers were downloaded to scratch, hash-checked, archive-extracted, and inspected. No installer, driver, input endpoint, service, or hardware command was run.

## Admission conclusion

All five remaining OpenBlade profiles have exact-model source evidence for the same Razer filter contract used by the typed SnapTap backend. They may be admitted as **source-backed experimental** profiles with `PhysicallyValidated=false` and, after the separate installation review below, `AutomaticInstallationAvailable=true`. Runtime admission must still find the exact host in both registry sources, the exact keyboard/control topology in one container, trusted installed packages for the model-specific `RzDev` service and the shared `RzCommon` service, and the reviewed typed API. The per-model hashes below and the inspected `RzCommon.sys` 1.0.0.76 hash `64B72EF9CE409BB17D31096C119B68C4A6A02AB35B7535D763157A4E1A587E59` identify source evidence rather than runtime build allowlists. Windows catalog trust, signed model/service declarations and file membership admit compatible installed updates; observed content fingerprints invalidate stale sessions.

| Model | Exact Windows product name | VID:PID | Vendor product | RzDev version | RzDev SHA-256 |
| --- | --- | --- | ---: | --- | --- |
| RZ09-0483 | `Blade 16 - RZ09-0483` | `1532:029F` | 671 | 1.0.0.73 | `97766397880C87B6C606E040C53EB3779A77EEA8CA62154C4C3C2C80FD401535` |
| RZ09-0482 | `Blade 14 - RZ09-0482` | `1532:029D` | 669 | 1.0.0.73 | `556049CFA85B817A899D6D3EF5AF42C6D45F0198CA68CE69908C6D9FFA60070D` |
| RZ09-0530 | `Blade 14 - RZ09-0530` | `1532:02C5` | 709 | 1.0.0.78 | `597C281383D4C7F07A789E88DF92E7814DC7F8118AC5631D3BF4FE8E5E1A87EB` |
| RZ09-0328 | `Blade 15 Base Model (Early 2020) - RZ09-0328` | `1532:0255` | 597 | 1.0.0.71 | `0533D4A3B83243959254E05BA45B7290B6FB6CF6CCCF4F71FF255EFF08BE55E2` |
| RZ09-0484 | `Blade 18 - RZ09-0484` | `1532:02A0` | 672 | 1.0.0.73 | `AD50A47698F0FA882B0A44F5CC898A1A8A1B8D9213BFDFF629CF49F63872D180` |

## Vendor manifests and exact driver archives

The manifest hashes pin the exact bytes retrieved from Razer on 2026-10-20. `buildVersion`, `commit`, and build time are the manifest's own Git metadata.

| Product | Vendor manifest and SHA-256 | Build / revision / UTC | Exact driver archive | Bytes | Archive SHA-256 |
| ---: | --- | --- | --- | ---: | --- |
| 597 | [manifest](https://apps.razer.com/synapse/products/597/mw/manifest.json), `470060C15E4EC8813571934704C12E5DA8D902B0BF72788185E161224A14E03F` | 2608200948 / `c0877d27ea53c0c724bd71ca4df01007860db08c` / 2026-08-20T10:03:53Z | [PID0255Drv_v0.1.2.0.exe](https://app-assets.razer.com/files/synapse/devices/597/1.0.0.1/PID0255Drv_v0.1.2.0.exe) | 2,840,208 | `D16607DF41F3B3291412FFA2D4A6C2B99E28AD45E9F94FC8A4E4BA97CA67B6A6` |
| 669 | [manifest](https://apps.razer.com/synapse/products/669/mw/manifest.json), `FDAC0D0DB545557920888AAD03A4C3DDDC3B71C8C52C7DA0E4F82399A243AE62` | 2608200925 / `76a8f2534f94101018d4d3664f75cb6db83539d4` / 2026-08-20T09:40:48Z | [PID029dDrv_v0.1.1.0.exe](https://app-assets.razer.com/files/synapse/devices/669/1.0.0.0/PID029dDrv_v0.1.1.0.exe) | 1,104,776 | `5AE43C20F4270C879816410C9D05089FB03EC6226FB9C73B3E7029414F9E706A` |
| 671 | [manifest](https://apps.razer.com/synapse/products/671/mw/manifest.json), `2F71433A283242EE24966777F3EFA2EBAA42CEDE670D71E214F1A850A5EEC7E8` | 2610090414 / `dd891ee27b3290778f962f3f760eed2d25dbdca6` / 2026-10-09T04:24:20Z | [PID029fDrv_v0.1.1.0.exe](https://app-assets.razer.com/files/synapse/devices/671/1.0.0.0/PID029fDrv_v0.1.1.0.exe) | 1,104,824 | `26ECE7C4311937602F617544D2761C9C60DDB64C8372EA8A153B7BE69058FA3D` |
| 672 | [manifest](https://apps.razer.com/synapse/products/672/mw/manifest.json), `07111923155CE732E808A000B6270CC49100D6E43940AC88A5E0A0CFA1D397A0` | 2610090242 / `8cb1e111267df5b957c71709163d36fdd47c99f5` / 2026-10-09T02:50:34Z | [PID02a0Drv_v0.1.1.0.exe](https://app-assets.razer.com/files/synapse/devices/672/1.0.0.0/PID02a0Drv_v0.1.1.0.exe) | 1,107,592 | `1905D41BADC81778034BB25EC1C79455908175D264E3ED5E077175698B4E5176` |
| 709 | [manifest](https://apps.razer.com/synapse/products/709/mw/manifest.json), `B68FBACA3E7E8D0F8D5986951EF242065944A11D86D3DB30FACD823AB94CFE80` | 2609300318 / `7b731e07dc34fee1ba8fa9f36daf3a00b3207e99` / 2026-09-30T03:27:13Z | [PID02c5Drv_v24.0.0.0.exe](https://app-assets.razer.com/files/synapse/devices/709/PID02c5Drv_v24.0.0.0.exe) | 1,092,664 | `18EF4C37F0BE221FF03AEF21BF3285F1E50F9C75BBA6E58D886E9F2939C589CD` |

Every installer size and SHA-256 matched its exact vendor manifest. Each installer and extracted `RzDev` binary had a valid Windows Authenticode result with subject `Razer USA Ltd.`. All four model catalogs per package passed kernel-policy signature verification. Catalog-member checks passed for each model's SYS and INF set. The 0255 package is the exception in file selection: its four legacy `RzDev_0255_*.inf` files are catalog members; the parallel `RzDevU_0255_*.inf` siblings are not. The installation recipe must select only the legacy INF set and its catalog-covered file closure; it must not select or install those U INF files.

## Exact topology

Each package declares the same PID-substituted structure:

- DKM lower filter: `USB\VID_1532&PID_<PID>&MI_01`, `LowerFilters=RzDev_<pid>`.
- Built-in keyboard upper filter: `HID\VID_1532&PID_<PID>&MI_01&COL01`, `UpperFilters=RzDev_<pid>`.
- Mouse/control upper filter: `HID\VID_1532&PID_<PID>&MI_02`, `UpperFilters=RzDev_<pid>`, `ControlDevice=1`.
- Virtual collection: `RZVIRTUAL\VID_1532&PID_<PID>&MI_00&COL03`, with `mshidkmdf` and the same model service.

Every exact `RzDev` binary contains `RAZER\DeviceContol`, `RZCONTROL\VID_%04X&PID_%04X&MI_%02X`, and `RZVIRTUAL\VID_%04X&PID_%04X&MI_%02X&Col%02X`. The admission profile can therefore require one `RZCONTROL\VID_1532&PID_<PID>&MI_00` endpoint whose ancestor is `USB\VID_1532&PID_<PID>&MI_02`, plus one `HID\VID_1532&PID_<PID>&MI_01&COL01` keyboard in the same non-empty container with USB MI_01 ancestry and exactly the matching `RzDev_<pid>` upper filter.

The per-model NormalDriver archives do not include `RzCommon`; Synapse supplies that shared component separately. This absence is not incompatibility evidence. Runtime admission must verify the actual shared driver and control child rather than infer them from the model archive.

## Product-specific SnapTap route

Each exact product build loads a content-addressed policy chunk containing SnapTap mappings. Its SnapTap flow calls `registerDeviceAsync(...rzDevice.device)` on the input-event module; that module calls `addUsbDevice(e)` with its default `skipFilterDriver=false`, followed by `registerInputNotification(e)`. The shared helper contains an `addUsbDeviceWithoutFilterDriver` alternate branch, but the SnapTap flow never selects it.

| Product | Exact policy chunk | Bytes | SHA-256 | SnapTap input registration byte offset |
| ---: | --- | ---: | --- | ---: |
| 597 | [6737.6da70458ec9e64b00f37.js](https://apps.razer.com/synapse/products/597/mw/6737.6da70458ec9e64b00f37.js) | 1,957,293 | `C6E6E76DFB61F960913E22B864A31273280547E21E1462DE3138F096D2DB56EA` | 1,062,832 |
| 669 | [6737.439ff1a12463933a6346.js](https://apps.razer.com/synapse/products/669/mw/6737.439ff1a12463933a6346.js) | 1,957,041 | `D1AC2212AED5491D3EAB69ABAFA270B963A2336E859DF6BFBF9ED9A455E3791B` | 1,062,628 |
| 671 | [6737.e123a2b3f0516116025b.js](https://apps.razer.com/synapse/products/671/mw/6737.e123a2b3f0516116025b.js) | 1,953,339 | `54D3A9BBEC83568B02342AA54FF47A94EE55A48ECA85B5D9F28D60D01A0B1A9A` | 1,311,551 |
| 672 | [6737.e50bb836b878190315d7.js](https://apps.razer.com/synapse/products/672/mw/6737.e50bb836b878190315d7.js) | 1,957,301 | `651F63DCC0EFC92B0FD7496EE70751707B5F650F20AF5C01C3255B61817C1997` | 1,062,882 |
| 709 | [6737.d34a3f9a638030f35c6a.js](https://apps.razer.com/synapse/products/709/mw/6737.d34a3f9a638030f35c6a.js) | 2,379,746 | `4758FB7E2D8296564FB9CE971F6DCE3D580ED5407EEF07A69E1C362A04FF28C0` | 1,082,007 |

## Filter ABI result and limits

The independent static [filter ABI report](snap-tap-filter-abi.md) records the callback review. The 029D, 029F, and 02A0 version-73 filters have byte-identical executable sections. The 02C5 version-78 filter matches the reviewed 02C6 contract across the normalized interception, callback, association, registration, input, original-output, wrapper, and control-creation functions. The older 0255 version-71 filter uses different compiler plumbing but preserves query-interface GUID `{14CC0596-CFA5-4379-A5AB-FA24E681D040}`, size `0x28`, version 1, keyboard type-2 callback registration, per-device association, original-output bypass, and cleanup restoration.

This establishes a common static contract with the reviewed `RzCommon.sys` API for all five exact-model filters. It does not establish physical A/D behavior, hot unplug, suspend/resume, or installed topology on any of these laptops. It grants no automatic installer, bundling, redistribution, or physical-validation status.

## Complete installation recipes

Reviewed 2026-10-26 without installing or loading any driver. Each original Razer carrier above is size/hash pinned and Windows Authenticode verified against its reviewed signer. The fixed NSIS non-solid LZMA1 data blocks produce only the selected catalog, INF, SYS and, for 0255, co-installer files. Extraction never interprets an NSIS script or executes the carrier, DPInst, Removeinf, plug-ins or its uninstaller. Exact block offsets, compressed sizes, output sizes/hashes and catalog signer pins are in `RazerKeyboardDriverVendorRecipes.cs`. Each block uses the reviewed 8 MiB dictionary; compressed and expanded members have independent 2 MiB bounds. The managed decoder is pinned SharpCompress 0.50.7 with its license shipped in the application payload.

The generic common package comes from the existing pinned catalog asset `Razer-02C6-Driver-Stack-1.0.0.78-1.0.0.76.zip` (227351 bytes, SHA-256 `533F8464A6A3ED328ABDADAC2DF2619859A6151FC17E775F46FCE310A682F8C5`). Setup verifies the complete ZIP and its inventory, then selects only `rzcommonu.inf`, `rzcommon.cat`, `RzCommon.sys` and the unchanged notice. No 02C6 filter is extracted or submitted on these models.

This pairing is established by the signed common INF's generic `RAZER\DeviceContol` hardware match (line 41), RzCommon service (51) and SYS declaration (47/59); every exact model filter creates that control child and exposes the independently reviewed query/callback contract. The [filter ABI review](snap-tap-filter-abi.md) establishes the shared hook API, output bypass and restoration behavior. A common binary hash by itself would not establish this applicability. All five common catalog/member checks and each model's catalog closure were verified natively. There is no newly published or bundled vendor binary and no broader redistribution claim.

Each model retains five separate package directories. Stage VCon, common, DKM, keyboard and mouse before binding DKM, VCon, keyboard, mouse and common. Exact host, physical topology, existing image/filter checks, fresh admission, scoped common targets, durable intent, restart/ambiguity stop and independent bound-package readback remain shared requirements. Role-specific INF names are explicit, so 0255 cannot silently select a U INF. Its four catalog-covered `WdfCoinstaller01009.dll` copies (1721576 bytes each, SHA-256 `18487B4FF94EDCCC98ED59D9FCA662D4A1331C5F1E14DF8DB3093256DD9F1C3E`) complete the signed legacy packages. Their co-installer registration appears only in the Windows 6.1 sections; modern NTamd64.6.2 selects the corresponding service/filter sections without it. Signed INFs are unmodified.

These recipes allow explicit experimental setup on the remaining five models. Offline extraction, trust checks and simulated binding do not establish a successful physical installation, restart or recovery. Those results remain pending for each model. Runtime compatible-driver admission and the other capabilities of these laptops are unchanged.
