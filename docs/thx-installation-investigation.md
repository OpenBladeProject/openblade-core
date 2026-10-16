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

The current vendor definitions for RZ09-0328/-0482/-0483/-0484 supply no THX
activation or resources. A force toggle supplies neither missing endpoint
bindings nor correct tuning. New component builds require their own pins.

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
