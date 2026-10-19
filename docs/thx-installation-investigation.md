# THX installation compatibility investigation

Read-only inspection on 2026-10-16 used official Razer packages. No vendor
installer, driver, service, native client, or hardware API was executed.
Packages were unpacked as data; MSI tables and managed custom actions were
inspected statically. Nothing was installed or changed in the audio stack.

## Official packages

The current [product 710 manifest](https://apps.razer.com/synapse/products/710/mw/manifest.json)
(RZ09-0528) and [product 709 manifest](https://apps.razer.com/synapse/products/709/mw/manifest.json)
(RZ09-0530) both reference the same rznb11 package:

- [THX APO 4.2.29.1159 installer](https://app-assets.razer.com/files/synapse/devices/common/THX/THXAPO_rznb11_v4.2.29.1159-9d162a6-i1974a_MSSIGN-0ae022fb.517bae6e.exe),
  SHA-256 `0154A94D60D68882E92BF03DC1265A8C82FE8432B64DD348438862194E89E32B`.
- rznb11 properties updater 4.2.37.1845,
  SHA-256 `8333667C11EFFC71ED09BBECB439F29C0642385F70C835287BE00F04C02BD6C8`.
- rznb11 patcher configuration updater 4.2.37.1845,
  SHA-256 `04F4D4A6A3F69391347BF08512FA5DC2C918E7443B5EA33024DFE1486DEB12AE`.

The [product 736 manifest](https://apps.razer.com/synapse/products/736/mw/manifest.json)
(RZ09-0581) supplies rznb12 properties/patcher updaters, but neither its current
nor inspected cached manifest supplies a base rznb12 APO installer URL. The
signed, installed 4.2.41.2499 stack was inspected for comparison. An installer
URL was not inferred from another product's filename.

All downloaded hashes matched their manifests. The APO installer and four
configuration updaters had valid THX LTD. signatures. The common native client
1.1.1.0 had a valid Razer USA Ltd. signature and SHA-256
`8E8F6923624762935D70B72F58BEC00808DB84DF740340047919B53331130A0A`.

## What the installer checks

The rznb11 MSI metadata declares `ProductIds=["rznb11"]`, `Devices=[]`,
`VIDPID=null`, `IncludeClientApp=false`, and `UseSetupApi=true`.
The empty device list does not mean unrestricted installation.

In the extracted managed custom action, `CustomActions.PreInstall` enumerates
Realtek audio hardware and calls `WinUtils.Utils.GetTHXAPOSupport`.
`GetTHXAPOSupport` takes the first matching Realtek device, extracts its eight
`SUBSYS_` characters, and checks whether they occur in the extension INF. If
support is absent, the installer reports "Device Not Supported by THX System
APO" and skips driver installation. Its install path binds the extension INF,
installs the remaining components, rescans/restarts audio, and starts the
product-specific service.

The signed rznb11 extension INF binds Realtek codec 0298 to subsystem IDs
`1A582023`, `1A58300E`, `1A58300F`, `1A58300B`, and `1A58300C`. Its sections
select PI4110, SO6110, KI8110, and KI8100 speaker tuning. The KI8100 entries
are explicitly marked testing-only in the vendor source. The SO6110 comment
names RZ09-0528. Product 709 identifies Piper11 / Blade 14, matching the INF
PI4110 Blade 14 (2025) branch; that comment leaves the RZ09 model as a
placeholder. Its expected subsystem ID is `1A582023`, which still needs
physical or installed-stack confirmation for RZ09-0530. KI8110 / RZ09-0529
belongs to the separate product 711 / Blade 18 branch in this shared package.

The INF writes `THX_PRODUCT_ID=rznb11`, speakers/headphones endpoint and
signal-flow IDs, and `VEN_THX&AID_rznb11APO4` / `VEN_THX&SID_rznb11SVC`
components. The service INF installs `thx_rznb11_service` using
`thx_rznb11_svc.exe`.

## Why the stacks cannot be interchanged

The installed rznb12 extension uses different Realtek/SoundWire codec IDs,
product/service/APO/HSA identifiers, and PI4120/SO6120/KI8120 tuning. Both
families expose speakers/headphones and the shared v4 control ABI, but their
APO/service binaries, endpoint bindings, properties, and speaker processing
coefficients differ. A matching common data blob does not establish stack
compatibility.

The APO and service are Windows audio components; the THX client association
is separate. Together with the Synapse-closed validation in
[THX audio](thx-audio.md), this supports controlling an already installed,
correctly matched stack without Synapse running. Retention after Synapse
uninstall and audible processing outside the confirmed RZ09-0581 built-in
speaker spatial check remain unvalidated.

OpenBlade now admits experimental manual controls and application routing for
exact RZ09-0528 and RZ09-0530 identities with their own reviewed rznb11 base stack and signed-INF
codec match. The 709/710/736 middleware native ABI and property operations
are byte-identical around the reviewed calls. Each model uses its own speaker
factory EQ vectors; headphone vectors are identical in the three definitions.
Only RZ09-0581 has physical round-trip evidence. RZ09-0528 and RZ09-0530 remain
source-backed experimental support; no native calls or writes on those models
occurred here, and their support is not production-ready.

The reviewed Synapse middleware resources for RZ09-0328/-0482/-0483/-0484
do not supply a THX installer. The three 2023 models use the separate legacy
THX service described in [THX audio](thx-audio.md); RZ09-0328 uses Dolby Atmos.
A force toggle supplies neither missing endpoint bindings nor correct tuning.
New component builds require their own pins.

No machine entitlement gate was found in the inspected MSI/custom-action
path; this does not establish that native code has none. These packages are
not bundled with OpenBlade. Altering a signed INF would invalidate its
catalog relationship and is outside this investigation.

## Active configuration admission

The signed rznb11 4.2.37.1845 properties updater embeds its archive as a
contiguous generated stack byte array. Static reconstruction, without running
the updater, recovered a valid 481,431-byte ZIP with 378 entries and clean
CRCs. Its SHA-256 is
`4FDDEEF868014DB94B902475CA2DB1919E8A902D027CFCE8A7ED691E403319EF`.
This is the reviewed active `ProgramData\thx\rznb11\thx_rznb11_props.dat`
payload, used alongside the pinned 4.2.29.1159 driver packages.

The rznb12 updater was independently reconstructed into a valid 478,742-byte,
377-entry ZIP, exactly matching the installed active archive. Its SHA-256 is
`E2E69163AFFEAE1F4FD19E347EB92E63CA7CAA32C13E8E1500AA459D76823031`.
The reviewed version-13 `surround_714_config.json` has SHA-256
`1AC5DC1DD9B6E60843E6E3076DC94F4C2492D9DB02600442AF276FD6A9CCFD6C`.
The signed rznb11/rznb12 patch updaters share the same code and JSON constants.
Static reconstruction of the version-13 configuration reproduced the exact
1,243-byte CRLF JSON and hash above. No updater or vendor native code was run.

DriverStore presence alone is insufficient: the service uses active files in
ProgramData. OpenBlade pins both immutable active files as well as the complete
driver packages. Mutable `properties.json`, endpoint values, and preset
selection files are excluded from hash admission because legitimate control
changes update them. Unknown immutable payloads fail closed.

## Standalone provisioning follow-up (2026-10-19)

A fresh read of product 709/710/736 manifests retained the same artifact pins.
The catalog now records separate rznb11 and rznb12 stack descriptors, including
all seven supported-model applicability decisions. The product 736 manifest
still has no base rznb12 APO installer; its two configuration updaters are not a
complete driver package. A bounded follow-up also found no audio installer on the
[RZ09-0581 support downloads page](https://mysupport.razer.com/app/answers/detail/a_id/20585)
and no result in the
[Microsoft Update Catalog exact-version search](https://www.catalog.update.microsoft.com/Search.aspx?q=4.2.41.2499).
These findings describe the sources reviewed, not every possible acquisition route.
The THX installers do not contain the base audio prerequisites. RZ09-0528 and
RZ09-0530 use a matching Realtek HDA codec package; RZ09-0581 instead uses
Windows inbox SDCA audio over Intel SST. The reviewed prerequisite source and
complete root match sets are recorded in the [catalog evidence](https://github.com/OpenBladeProject/blade-driver-catalog/blob/60adc8e510097344f34244fd113dd4be949a9d81/evidence/thx-base-audio/prerequisites.json).
The experimental setup recipe imports the pinned base root and its declared
CopyINF closure only after exact host/controller and installed-binding admission.
Windows inbox components are checked through servicing catalogs, not bundled.
Clean installation and reboot behavior remain physically unvalidated.

The 709/710 base-installer resources explicitly specify `/s /norestart`. Static
inspection of both signed rznb11 configuration updater entry paths shows fixed
zero-argument deployment. They write the previously pinned immutable outputs
and invoke a stop/start command for `thx_rznb11_service`, ignoring its result.
Independent file and service checks are therefore required.

OpenBlade's experimental provisioning path admits only a pristine THX state on
exact RZ09-0528/RZ09-0530 hosts and codecs, and now the separately reviewed
cached 1621 build on exact RZ09-0581. It does not use the vendor's legacy
2.x/3.x removal path to repair or upgrade an existing installation. Every launch
has a durable intent, and the resulting stack/configuration/service must pass
independent verification. The implementation and simulated tests are separate
from physical installation evidence; no vendor installer or updater was run in
this investigation.

## Expanded source search (2026-10-19)

### Recovered rznb12 installer

Read-only inspection of the Windows Installer cache recovered a complete signed
rznb12 base installer, identified by its embedded original filename:

`THXAPO_rznb12_v4.2.37.1621-5117e8e-i2546a_MSHLKSIGN-2255eea3.3219a439.exe`

The 53,720,208-byte cached executable has SHA-256
`32392A63FC704DFC4BFBE4F33ED038773BBDE21AC88C61BAEEA013E3F98E8CF2`.
Its Authenticode signature is valid, with THX LTD. certificate thumbprint
`B23FAAE4D5ADEDA77BCCD9F12F32C92D3FF755D5`.

Static extraction of its cached MSI cabinet recovered the APO, extension and
service packages at version 4.2.37.1621, dated 2026-01-23. All nine non-catalog
files, including the INFs, match their signed Microsoft Windows catalogs. The
extension binds the `30101A58` SoundWire subsystem used by RZ09-0581 and selects
`SO6120_speakers-default`. Its 340-entry properties archive contains the
PI4120, SO6120 and KI8120 presets; its SHA-256 is
`FF587D74CE7763FCB2B0150B62A67237844B688647CE0434219B814608E12C07`.
The installer metadata specifies `ProductIds=["rznb12"]`, exact hardware IDs,
`UseSetupApi=true` and `IncludeClientApp=false`.

The original Razer URL is absent from the inspected installer registration.
HEAD against its inferred filename in the common THX directory returned HTTP
403; this does not establish whether an object exists there. The owner confirmed
that existing written Razer permission covers original THX installers as catalog
release assets. The catalog therefore provides a separate, exact pinned delivery
route for this unmodified cached installer, with its original license notice.
The permission document is retained outside Git and was not independently reviewed.

Static review found no Synapse runtime dependency in the standalone MSI/service
path. The wrapper is a .NET Framework 4.8 program that extracts an elevated Burn
to a user-writable location (LocalAppData for rznb12, Temp for rznb11). OpenBlade
never executes either wrapper. It hashes, length-checks and signature-verifies
the locked original carrier, extracts the fixed raw-DEFLATE range as data into
protected staging, then independently hashes and verifies the complete signed
inner Burn before executing it. The fixed arguments are `/quiet /norestart` plus
a protected absolute `/log` path. Detached cached Burn engines lack the attached
MSI/CAB payload and are not valid replacement installers.

| Family | Compressed offset | Compressed length | Complete inner length | Complete inner SHA-256 |
| --- | --- | --- | --- | --- |
| rznb11 | 569682 | 50445093 | 51037408 | `819B1504B025105589B5899CC09BB612B7AD83F8DED8DE0D087F81D4085542ED` |
| rznb12 | 577088 | 52553413 | 53145160 | `DE47E110A3E528249EAF59FA7932C1A568183A38F7B52246EE395A0418B14620` |

Both inner signatures use the reviewed THX certificate above. The installer can
report success after ignored SetupAPI failures, so exit zero never replaces the
independent package/catalog, active-file and running-service checks. The durable
journal binds recovery to the complete recipe, arguments, extraction and installed
file pins. Uncertain results pause; another recipe cannot resume them.

OpenBlade admits cached 1621 as a separate complete stack, retaining 2499 and
rejecting mixed package members. The exact 1846 active configuration and native
client remain required. The two builds' speakers/headphones endpoint definitions
and exposed property contracts match, but their archive schemas and other content
differ materially. No vendor manifest explicitly pairs 1621 with 1846. Admission
therefore additionally requires complete successful reads of both endpoints and
preset lists before any mutation, tied to the freshly verified stack identity.
This is experimental source-backed admission, not a pristine-install, native-runtime
or audible result. Synapse must be absent for the eventual fresh-install acceptance
test; merely closing it cannot establish that result. No vendor executable was run
and the working installed 2499 stack was not replaced during this review.

### Advertised Synapse manifest history

The [official installer loader](https://apps.razer.com/installer/assets/index.325f8759.js)
resolves each product's `installer-manifest.json`, then its advertised latest
and archived manifest paths. Following those references inspected all seven
[product 736 versions](https://apps.razer.com/synapse/products/736/mw/installer-manifest.json),
1.0.0 through 1.0.6. None advertises a base APO installer. Versions 1.0.0-1.0.3
carry native client 1.0.2.1; 1.0.4-1.0.5 carry 1.1.1.0; 1.0.6 adds the known
rznb12 configuration updaters.

The advertised histories for
[669 / RZ09-0482](https://apps.razer.com/synapse/products/669/mw/installer-manifest.json)
(1.0.2),
[671 / RZ09-0483](https://apps.razer.com/synapse/products/671/mw/installer-manifest.json)
(1.0.0-1.0.1) and
[672 / RZ09-0484](https://apps.razer.com/synapse/products/672/mw/installer-manifest.json)
(1.0.1) contain no THX/audio resources. Current beta 736 and production 737
also supplied no alternate rznb12 base resource. These are bounded feed
results; they do not prove the packages are absent from all Razer delivery
channels or Windows Update.

### Windows Update and legacy OEM tuning

Static inspection corrected the earlier assumption that the exact Realtek
packages establish only codec identity. Their signed `RTAIODAT.DAT` files also
contain per-subsystem Razer codec profiles:

| Model | Exact codec subsystem | Realtek package | OEM codec profile |
| --- | --- | --- | --- |
| RZ09-0482 | `1A582020` | [6.0.9484.1](https://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid=d5987bbd-d8bd-4ebe-95e6-2c521b13baaa) | `Razer_2020` |
| RZ09-0483 | `1A583006` | [6.0.9601.1](https://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid=fc8c3fe8-290e-4984-af52-75020538e18d) | `Razer_3006` |
| RZ09-0484 | `1A583007` | [6.0.9601.1](https://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid=01c12b71-a0f8-40ff-8be0-7b9c4225ed5a) | `Razer_3007` |

The 6.0.9484.1 data file is 54,704,172 bytes, SHA-256
`E84591EF1106343CE3200795CDFADDC4D6347F6F86E8E28012CEC82F130E5A32`.
Both 6.0.9601.1 downloads contain the same 57,817,855-byte data file, SHA-256
`98509C3F66CA9D20B12DE14A8063C25502C439840193D501E80A857EA510F08B`.
All three data files passed membership verification against their signed Windows
catalogs.

The driver-backed parser review below corrects an earlier shifted interpretation
of this format. Each file starts with a 16-byte `MDTR` header: file length at
`+4`, hardware count at `+8` and profile-descriptor count at `+12`. Hardware
rows begin at byte 16 with a 28-byte stride. The subsystem range is at row
`+8/+12`; the selected zero-based profile index is at `+20`. Target row indices
2402, 2376 and 2374 select profiles 2428, 2397 and 2394 respectively.
The trailing `0x250B` at row `+24` is not read by the inspected selection loop.

The descriptor table starts at `16 + hardwareCount * 28`. Each 28-byte descriptor
contains payload offset at `+0`, count at `+4`, size at `+8` and its ASCII name
at `+12..27`. Starting with the name and reading the following offset fields
incorrectly selects the next descriptor's payload. The corrected Razer payloads
are byte-identical across 6.0.9484.1, 6.0.9601.1 and 6.0.9909.1:

| Profile | Total bytes | Record | SHA-256 |
| --- | ---: | --- | --- |
| Razer_2020 | 6,900 | One RTKAMPI record: 6,864 data bytes + 36-byte header | `BAC1AC6C25DC7AF6B51EC7AD0A474FA4D87E54B0096E9A1A334F7776B4A43D3C` |
| Razer_3006 | 18,324 | One RTKAMPI record: 18,288 data bytes + 36-byte header | `42D19433C85388ABE861BF4A850D6B39B193905F60FA7B0824F99BBAABCDFC6E` |
| Razer_3007 | 27,748 | One RTKAMPI record: 27,712 data bytes + 36-byte header | `BD0731A047C8EA9F68F82212CB8320AC2CD2995CEBE478C4BF896908AD1480E7` |

The earlier RTSPKPT, RTEQEX and RTKHDRC payload attribution was wrong: those
neighboring descriptors are MSI_13D7, Acer_1654 and MSI_13D3. The original full
DAT hashes and exact Razer profile-name mappings remain correct. These corrected
records establish distinct Realtek OEM data, not THX acoustic `deviceEQ` or a
complete THX configuration. The AMD `HDXACPRazer.inf` declares the THX MFX
capability GUID `{E82A5EE8-44D2-44C1-897E-AA5AFBF146BA}`; the reviewed Intel
`HDXSSTRazer.inf` does not. Ordinary `ks.inf`/`wdmaudio.inf` Include/Needs are
present. Neither reviewed Razer INF establishes THX-specific child enumeration
or endpoint binding. The AMD GUID is not an alias registered by THX 2.6.5's
reviewed APO INF; its host/offload CLSIDs differ. These bounded findings do not
prove that no runtime integration exists or that a separate extension is required.

### Legacy THX tuning and binding review

The signed [THX SCU 2.6.5 package](https://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid=d7c9ca5f-77ae-4681-a1b4-78453818d1c4)
contains `spatial-config-util.exe` 4.5.5.0, SHA-256
`E0C8C2B0BF40A25CFFD5545A8043E31F2D61FD4CD7A82B229CDE53F269943CF9`.
Its embedded default `devices-V2.yaml` selects actual model-specific THX acoustic
payloads; they are not merely profile names or the adjacent generic fallback file.
The generic fallback is explicitly rejected by the normal execution path.

| Model | Subsystem suffix → profile | Selected speaker profile | SpeakerPath SHA-256 |
| --- | --- | --- | --- |
| RZ09-0482 | 2020 → PI490 | profile-20230502 | `A694CC6E87985CF3EF58AAA2CF1E00FCCA4F24FE02EE464AC164C72986F4AF68` |
| RZ09-0483 | 3006 → SO690 | profile-20221107 | `94A947281EF38E06C8C81F26BBAA3A64640323FD50F264E171FC52401EBD36E2` |
| RZ09-0484 | 3007 → KI890 | profile-20221122-PVT-red | `ECECE0925FC8FA43D4102A9E49BB7EE1A7F8A9B3F6233F4D50A71C788A0FC767` |

Static extraction validated the embedded gzip/base64 JSON bytes, including shared
headphone and crosstalk processing. On the normal INF branch, SCU reads Realtek
subsystem information, chooses the Razer profile and writes a merged
`ProgramData\THX\thx_spatial.conf.json`. The `_ISST_SSP` branch adds `-oem`
and selects by OEM product name instead. The actual factory child nodes and
final serialized configuration on each laptop are still unverified.

The later factory-export review below recovers a catalog-authenticated THX
extension that declares the exact-model SWC children and normal SCU branch.
The remaining blocker is the complete matching endpoint FX/APO binding and
installation/rescan/restart contract. The earliest visible exact Intel Realtek
[6.0.9452.1 package](https://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid=bf428f32-bf81-4c77-b0b4-6a9a36701cd5)
does not supply that contract. The modern installer's absent-device fallback
only stages an INF; it does not supply legacy SWC creation semantics.

An original matching integration package or factory SetupAPI logs together with
original signed INFs and actual codec/THX parent-child nodes can resolve the
specific gap. No extra acoustic tuning package has been shown to be missing.
Do not fabricate SWC nodes, modify signed INF directives, or write guessed
endpoint FX registry values. Fresh legacy provisioning stays blocked.

The rznb12 Catalog search covered its component IDs, extension ID, recovered
filename and versions 4.2.37.1621, 4.2.38.1962 and 4.2.41.2499 without finding
an exact package. The generic THX 4.2.38.1967 result explicitly targets
`SWC\VEN_THX&SID_RZNB11SVC`; it is not an rznb12 substitute. These bounded
search results do not establish whether Windows Update can privately target
another submission to the exact device.


### Original Razer download route

The official model download listings for [RZ09-0482](https://mysupport.razer.com/app/answers/detail/a_id/13732),
[RZ09-0483](https://mysupport.razer.com/app/answers/detail/a_id/9743) and
[RZ09-0484](https://mysupport.razer.com/app/answers/detail/a_id/9676) did not expose
a separately named Realtek/THX package in indexed official-page content. Direct
page requests reached Razer's pre-check; this was not a complete live-link audit.

The [RZ09-0482 AMD APU guide](https://mysupport.razer.com/app/answers/detail/a_id/13229)
links the original [APU package](https://rzr.to/oGIdKJ), an 888,405,128-byte ZIP.
HTTP range inspection recovered its complete 417-entry directory and selected
small audio INF/manifest entries without downloading or executing the full bundle.
`amdacpbus.inf` stages `amdacpbusext.inf`, which targets
`PCI\VEN_1022&DEV_15E2&SUBSYS_201C1A58&REV_60`, copies `acpcfg0001.dat`, and
registers `AcpConfigDatPath`. The inspected ACPBus/ACPAfd/ACPBtAfd/HDMI audio
INFs and installer manifests contain no THX-specific child/binding declaration.
This is an additional ACP integration source, not proof of a THX binding rule or
of that PCI identity on an actual RZ09-0482. Binary ACP behavior remains unexamined.

### Recovery images and the recovered THX child extension

Razer's [missing THX codec guide](https://mysupport.razer.com/app/answers/detail/a_id/3985/)
recommends a factory recovery image after Windows reset or clean installation.
This makes the image a promising source for original integration packages; it
does not establish which files it contains or independent installability.
The [official download process](https://mysupport.razer.com/app/answers/detail/a_id/5997/)
requires a Razer ID, a registered laptop and its serial, and supplies a link valid
for 48 hours. No exact RZ09-0482, RZ09-0483 or RZ09-0484 image was obtained in
this investigation. The public support listings exposed no image manifest.

A public [factory-driver export](https://github.com/Torahime3/razer-blade-14-thx-driver-restore/tree/4f3451e246a93d48dabf8e2140ce3aebbd2a08f4),
although described by its uploader as a 2022 Blade 14 backup, contains a newer
[THXHDAudio.inf](https://github.com/Torahime3/razer-blade-14-thx-driver-restore/blob/4f3451e246a93d48dabf8e2140ce3aebbd2a08f4/drivers/thxhdaudio.inf_amd64_a639441cfa75798e/THXHDAudio.inf)
with exact codec matches for all three supported 2023 models. This is a signed
vendor artifact recovered through a public mirror, not a verified recovery image
from one of those laptops.

| Model | Exact codec match | THX install section |
| --- | --- | --- |
| RZ09-0482 | `HDAUDIO\FUNC_01&VEN_10EC&DEV_0298&SUBSYS_1A582020` | `DeviceExtension_Install_RT_SKU_C` |
| RZ09-0483 | `INTELAUDIO\FUNC_01&VEN_10EC&DEV_0298&SUBSYS_1A583006` | `DeviceExtension_Install_RT_SKU_A` |
| RZ09-0484 | `INTELAUDIO\FUNC_01&VEN_10EC&DEV_0298&SUBSYS_1A583007` | `DeviceExtension_Install_RT_SKU_A` |

The extension has DriverVer `06/17/2023,2.6.5.0`, ExtensionId
`{17B56683-7F95-4BAB-8031-AB5DE114A4CC}` and catalog `THXHDAudio.cat`.
Both selected `.NT.Components` sections declare THXAPO, THXSVC, THXHSA and
THXSCU children with component IDs `VEN_THX&CID_THXAPO`,
`VEN_THX&PID_THXSVC`, `VEN_THX&PID_THXHSA` and `VEN_THX&PID_THXSCU`.
They select the normal AVX2 APO and subsystem-based SCU, not the separate
SSE2/ISST_SSP path. The extension adds the OutAPO/HPAPO topology settings,
THX endpoint device types and `%ProgramData%\THX\thx_spatial.conf.json` path.

The Git export normalized the INF to LF. Its downloaded bytes failed catalog
membership. Restoring CRLF, with no directive changes, recovered the exact
SHA-256 already present in the original catalog. Windows SDK SignTool
`verify /kp /c` then passed against the Microsoft Windows Hardware Compatibility
Publisher chain (signer `B878D8EB696CF3D4505E2F6641C57AF9062EC51A`, timestamp
2023-06-21). The original catalog passed kernel-policy signature verification
separately. Byte pins are:

| Artifact | Bytes | SHA-256 |
| --- | --- | --- |
| Downloaded LF INF, not catalog-valid | 28,868 | `5E800A76DD0DC284A048E7301433765D114640CFF82EC87D65D44DC375AF3D14` |
| Recovered original CRLF INF, catalog-valid | 29,311 | `6FE831463C60FD2C1ADA9207FD63607491BEA87F70AE5D48B9982F32B6571D72` |
| Original THXHDAudio.cat | 25,994 | `FB496566667F6AE7C415A96BFE3D97B14BD63C1681C19B9906A21EBF0AFFE799` |

This closes the missing child-enumeration declaration and SCU branch questions,
but not the full endpoint FX chain. The CompositeFX CLSIDs elsewhere in this
THX INF belong to different AE34 Intel SSP IDs. They must not be attributed to
the three codec sections above.

The same export's [hdx_razerext_thx_rtk_gen4_kd.inf](https://github.com/Torahime3/razer-blade-14-thx-driver-restore/blob/4f3451e246a93d48dabf8e2140ce3aebbd2a08f4/drivers/hdx_razerext_thx_rtk_gen4_kd.inf_amd64_37ae9ddc6f6cd57c/hdx_razerext_thx_rtk_gen4_kd.inf)
provides a concrete separate Realtek/THX CompositeFX-chain example, with
ExtensionId `{C3A63EDD-2D27-4B66-B155-5E94B43D926A}`. Its original CRLF bytes
also pass `verify /kp /c` against `HDXRTExt.cat`; INF SHA-256 is
`67EACBBBB5D40916E1767456947E79D4D199EF1B2B307D3DADAC244EA49D654D`.
However, DriverVer `01/04/2022,6.0.9292.1` omits all three target subsystem IDs.
A matching newer extension or another exact-model signed binding source is
still required; the older package cannot be generalized by editing its model list.

A further official Catalog search found a shared Realtek 6.0.9909.1 codec CAB
for [RZ09-0482](https://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid=09e3be47-66f8-436d-ae59-fb32539a4556),
[RZ09-0483](https://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid=353e4569-423f-4fd6-be0f-9579d7fcf819)
and [RZ09-0484](https://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid=52814225-cea2-480e-8199-930c0922929f).
The original CAB is 13,638,225 bytes, SHA-256
`86C019C45400B3456E77A97D6DA29302C273423E3396255DE6FB7B8B64002EE6`.
Its 216 files contain base MEDIA codec content rather than extension packages.
The exact Razer INFs and `RTAIODAT.DAT` passed `verify /kp /c` against its
original `hdxrt.cat`; the INFs contain no `AddComponent` or `AddSoftware`.
This newer base package does not supply the missing FX extension.

The 6.0.9909.1 `RTAIODAT.DAT` is 62,753,315 bytes, SHA-256
`08653E16F4D5A7EA432538E6E480AD0F77D33D9878D5AAAE29C8A7781DD8C83D`.
Its corrected Razer payloads match the RTKAMPI hashes above. The corrected
payload offsets are 55,217,121 (2020), 54,836,781 (3006) and 54,766,993 (3007).
Whole-file ASCII, UTF-16 and binary-GUID searches found no explicit THX
CompositeFX, endpoint-setting or APO CLSID declaration. This marker result is
unchanged by the descriptor correction; it does not prove that computed or
indirect runtime integration is impossible.

Queries for the Realtek extension GUID, filename and exact codec IDs found no
extension entry. Control queries for IDs in the recovered signed 2022 extension
also returned only MEDIA packages. These searches therefore cannot prove that
the needed newer extension never existed or is unavailable through privately
targeted Windows Update delivery.

The matching signed `RTKVHD64.sys` 6.0.9909.1 is 6,818,344 bytes, SHA-256
`48C525F8E58DA40EE54BAE8EA95C44D6944A9D77E9EE98C500B90284257312CD`.
It passed kernel-policy catalog membership verification. Bounded static analysis
of six `.pdata` function extents used Ghidra with full-program analysis disabled;
no vendor code was executed or loaded as a driver.

The MDTR consumer at image VA `0x1406295D4` establishes the corrected row and
descriptor layout above. `DCH_THX` consumers at `0x1404564E8` and
`0x140473E6C` feed internal policy flags; no inspected dataflow connects those
flags to the required THX CLSIDs. Interface registration at `0x1405E0D08`
opens supplied `InterfaceSetting` sections and passes their contents into
registry helpers. Its mixed-case `{D04E05A6-594B-4fb6-A80D-01AF5EED7D1D}`
property-name comparisons are not evidence of THX CompositeFX values.
A second bounded trace resolves the two helpers: `0x1405DEA18` recursively
copies existing source registry values and subkeys; `0x1405E3988` sets or deletes
caller-supplied typed values. The copy helper can carry CompositeFX properties
13/14/19/20 when already supplied. Neither inspected helper derives THX CLSIDs
from RTKAMPI tuning. The interface path uses `InterfaceSetting`, a conditional
`SysRecFx` capture fallback, `%ws_ext` sections and separate `FriendlyName`
handling.

The base adapter callbacks are now resolved. Constructor `0x140589504` installs
vtable `0x1401E02F8`; caller `0x14012E0CC` stores that adapter at device `+0x1A8`.
Slot `+0x38` selects wave names through `0x140593D10`; slot `+0x40` selects
topology names through `0x140593940`. Confirmed selectable pairs include
`RearLineOutWave2` / `PrimaryLineOutTopo`, `SecondaryLineOutWave` /
`SecondaryLineOutTopo`, and `RearLineOutWave3` / `SingleLineOutTopo`, alongside
the SST and HAP variants used by the recovered THX extension. These functions
select names from endpoint flags; they do not construct the FX property values.

Both selectors first consult an override at adapter `+0xC08`. Its creator,
`0x14059C620`, dispatches by an OEM-table value at `+0xC8C`, separately from
codec identification. The earlier codec-family attribution was incorrect.
Selector `0x14013B34C` matches the Razer row at `0x14024F1A0` when
`device+0x15C >> 16 == 0x1A58`, selecting dispatch `0x2A` and ID `0x22`.
That dispatch calls factory `0x140475C14`, constructor `0x140473E6C` and vtable
`0x1401D3100`. Both name slots point to `0x14000FCD0`, whose verified bytes
`33 C0 C3` unconditionally return null. These two Razer methods supply neither
endpoint names nor FX values; the base selectors use their existing fallback.

The codec hardware ID establishes subsystem input `device+0x160`, while that
OEM table tests `+0x15C`. Initialization copies between them only when the
separate `+0x158` field is zero or `0xFFFFFFFF`. The identified Razer override's
behavior is established, but universal live selection and endpoint flags remain
unproved. The fallback names are not confirmed runtime selections for every
installation of the three laptops. This trace stops at that finite boundary.

The recovered THX extension already appends `OutAPO`/`HPAPO` settings to ordinary,
SST or HAP topology lists. Their shared sections supply device types and the
configuration path, rather than the complete effect CLSID chain. The remaining
acquisition target is an original exact-model signed integration package
supplying the matching render FX subtree and actual THX CLSIDs, with installation
provenance/order. Factory registry evidence should preserve the final
`InterfaceSetting` lists, referenced `FX\0`/`EP\0` subtrees and adapter/endpoint
identity; final endpoint `FxProperties` alone cannot prove provisioning. This
bounded trace does not exclude other runtime paths. Fresh legacy setup remains
blocked on that binding source and the installation contract.

If an exact-model recovery image becomes available, first inventory its ZIP and
OEM provisioning files, then selectively extract the audio DriverStore packages,
setup scripts and any retained SetupAPI logs. [wimlib extract](https://wimlib.net/man1/wimextract.html)
supports selected paths and split WIM inputs without applying the OS.
[Microsoft's setup-log guide](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/deployment-troubleshooting-and-log-files?view=windows-11)
identifies `Windows\INF\setupapi.offline.log`, `setupapi.dev.log` and Panther
as relevant setup evidence, if retained. Inspect registry hives as files rather
than loading them into the live registry. Preserve complete original packages,
catalog membership and exact-model provenance; archive integrity checks alone
do not verify driver signatures.

No recovery was booted or applied, and no driver, service, registry or audio
settings were changed. Legacy fresh setup remains `CandidateOnly`, `Blocked`
and `installableCandidate=false` until the remaining binding/dependency and
installation contract is established. Static recovery evidence would still
require a separate clean installation test with Synapse absent.

### Runtime prerequisites without Synapse

A static PE import audit covered the pinned native v4 client, rznb11 service/APO
and patch, cached rznb12 4.2.37.1621 service/APO and patch, all four 1845/1846
configuration updaters, and the legacy 2.6.5 APO/service/configuration utility.
Their inspected normal imports name Windows system libraries only; none declares
delay imports. The configuration updaters and `spatial-config-util.exe` are
native executables. These findings identify no directly imported Synapse runtime
or separate Visual C++ runtime requirement in those files; they do not exclude
later dynamic loads or prove successful fresh installation.

The legacy service INF launches `VSSrvInit.exe` through `AddSoftware`. That
42,080-byte initializer, SHA-256
`E2E15276061505C12A1540D4DDCF6788F1794C421385A43C6730DECD9679DCC0`, targets
`.NETFramework,Version=v4.0,Profile=Client`. The inspected modern MSI custom
actions and `THXSetup` target .NET Framework 4.8. Bypassing a managed outer
bootstrapper does not remove those inner custom-action requirements.

Static decompilation resolves the initializer's own setup sequence: it starts
`%SystemRoot%\System32\VSSrv.exe -service` without a shell, waits for that process,
then starts `VSSrv` if stopped and waits for `Running`, with two-second pauses.
Its registry access reads the optional log path; it contains no endpoint FX
writer or Synapse launch. The initializer does not inspect the child exit code
and its process/service waits are unbounded, so its exit alone cannot establish
successful setup.
Microsoft's [AddSoftware contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/inf-addsoftware-directive)
queues this software after device installation without guaranteeing when or
whether it runs. Consequently, a successful INF installation cannot by itself
prove COM initialization; any future recipe needs independent service and COM
readback before enabling controls.

The matching `VSSrv.exe` resource `REGISTRY/101/1033` contains HKCR COM-class
templates for its settings/APO-control objects and their type library; it does
not contain an endpoint `InterfaceSetting`/`FxProperties` declaration. These
templates are now linked to the native registration branch. Parser
`0x140056500` calls `0x140057BD0`, which applies resource 101 and the known
APPID substitution, sets `HKCR\AppID\{EF809770-D328-4410-9772-32775E710711}`'s
`LocalService`, and invokes service creation. Pre-registration `0x14005AFF0`
stops and deletes an existing VSSrv service; deletion can follow a reported stop
failure. Consequently, `-service` is a service-replacement operation, not a
read-only registration probe or an automatically retryable repair.

Creation at `0x140055A10` uses the quoted executable path, own-process service
type, LocalSystem default account and RPCSS dependency. It initially selects
demand start; the caller's `0x1400533B0` subsequently selects automatic start.
All nine object-map registration/category callbacks are verified no-ops at
`0x14000A040`. Module substitution callback `0x14004FC90` adds only APPID;
`0x1400582A0` supplies the known executable REGISTRY resource to the generic
ATL interpreter. Final helper `0x1400530A0` registers the type library, whose
loader first uses the executable and can fall back to same-stem `VSSrv.tlb`.
That sidecar is a dynamic boundary and is not assumed to equal the embedded
library. Optional post-registration callback `0x14022F750` is zero in the stored
image; runtime-null has not been proved.

This supplies a positive original service/COM setup contract, with no concrete
new endpoint FX binding source in the inspected paths. It does not prove all
process side effects, successful registration or complete THX setup. COM
registration and endpoint FX binding remain separate requirements; no native
registration command was executed here.

OpenBlade already requires Windows 11 24H2 or later. Microsoft's
[Framework installation table](https://learn.microsoft.com/en-us/dotnet/framework/install/on-windows-and-server)
identifies .NET Framework 4.8.1 as included in 24H2 and able to run .NET Framework
4 applications. This supplies the inspected framework prerequisites independently
of Synapse. OpenBlade's existing installer also provisions its pinned x64 Visual
C++ runtime; the import audit does not establish a new THX dependency on it. A
clean installation with Synapse absent still needs to validate dynamic loading,
service initialization, endpoint creation and audible processing.
