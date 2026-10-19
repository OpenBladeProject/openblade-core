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
The THX installers also do not contain the Realtek base codec driver.

The 709/710 base-installer resources explicitly specify `/s /norestart`. Static
inspection of both signed rznb11 configuration updater entry paths shows fixed
zero-argument deployment. They write the previously pinned immutable outputs
and invoke a stop/start command for `thx_rznb11_service`, ignoring its result.
Independent file and service checks are therefore required.

OpenBlade's experimental provisioning path admits only a pristine THX state on
exact RZ09-0528/RZ09-0530 hosts and codecs. It does not use the vendor's legacy
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

This establishes an older model-bound package, not a public acquisition source
or compatibility with OpenBlade's pinned 4.2.41.2499 runtime. The base version
and its installer behavior need separate review before admission. The original
source URL is absent from the inspected installer registration. HEAD against
its exact filename in the known Razer common THX directory returned HTTP 403;
that directory placement was an inference, and the response does not establish
whether the object exists. The cached binary was not executed or published.

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

For reproducibility, these files start with `MDTR`. Little-endian 32-bit hardware
and profile counts are at byte 8. The hardware table starts at byte 24 with a
28-byte stride; its first field is the subsystem and fourth field is the profile
index. The profile table starts at `24 + hardwareCount * 28 + 4`, also with a
28-byte stride and a 16-byte ASCII name. Records 2402, 2376 and 2374 select
profile indices 2428, 2397 and 2394 for the models above. The shared field value
`0x250B` has no established meaning and is not evidence of a THX selector.

The profiles contain distinct Realtek EQ, DRC, amplifier and speaker-protection
blocks. This establishes OEM codec tuning, not THX acoustic `deviceEQ` or a
complete THX configuration. Both Razer INFs also declare the THX MFX capability
GUID `{E82A5EE8-44D2-44C1-897E-AA5AFBF146BA}`. Their INF text does not declare
`AddComponent`, `AddSoftware`, `Include`/`Needs` or literal `VEN_THX` child
enumeration. The runtime driver could interpret additional data; absence from
the INF does not prove a separate extension package is required.

The generic THX SCU package invokes `spatial-config-util.exe` through
`AddSoftware` after its software component exists. Its
`generic-config-fallback-V2` configuration has empty `deviceEQ` objects. The
remaining gap is the runtime enumeration, binding and configuration path between
the exact Razer codec package and these THX components, including installation
order. No missing standalone OEM package has been established.

The rznb12 Catalog search covered its component IDs, extension ID, recovered
filename and versions 4.2.37.1621, 4.2.38.1962 and 4.2.41.2499 without finding
an exact package. The generic THX 4.2.38.1967 result explicitly targets
`SWC\VEN_THX&SID_RZNB11SVC`; it is not an rznb12 substitute. These bounded
search results do not establish whether Windows Update can privately target
another submission to the exact device.
