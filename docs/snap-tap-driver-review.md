# SnapTap driver review

Reviewed on 2026-10-20. Vendor files were inspected as data only. No driver was installed, no vendor code was loaded, and no input interception or driver mutation was performed.

## Published support and OpenBlade scope

Razer's [SnapTap support list](https://mysupport.razer.com/app/answers/detail/a_id/14658) describes support through Synapse 4 across its keyboards and laptops. Its [announcement](https://www.razer.com/newsroom/product-news/snap-tap-available-on-razer-blade-more-keyboards) specifies Synapse 4-compatible Blade laptops and explains that their processing runs in software. This supports a shared implementation; it does not specify the native driver ABI.

OpenBlade shares one A/D processor, codec, transport, hook session, supervisor and recovery journal across reviewed driver profiles. Exact host identities, PID, filter service and keyboard/control topology remain profile-specific. Runtime admission verifies the installed package, its model/service declarations, Windows catalog trust and file membership. The binary hashes below identify reviewed sources, not runtime build allowlists. Compatible installed updates retain access; changed content invalidates cached admission and active sessions. Installation availability and physical-validation provenance remain separate from protocol compatibility.

## RZ09-0528 / 02C6

The immutable catalog input is [revision 643d6985cd0a4357c5f6a99dea76087fb95850e6](https://github.com/OpenBladeProject/blade-driver-catalog/blob/643d6985cd0a4357c5f6a99dea76087fb95850e6/devices/rz09-0528/1532-02c6/1.0.0.78-1.0.0.76/manifest.json). The [published archive](https://github.com/OpenBladeProject/blade-driver-catalog/releases/tag/rz09-0528-1532-02c6-driver-stack-1.0.0.78-1.0.0.76) is 227,351 bytes, SHA-256 `533F8464A6A3ED328ABDADAC2DF2619859A6151FC17E775F46FCE310A682F8C5`. All 15 INF/SYS/CAT member hashes and lengths matched the catalog.

- `RzDev_02c6.sys` 1.0.0.78: `307940C7C5AB537F19F0A33ADA592D2D6DE5ECADB13D748019A8F0E13816BA82`.
- `RzCommon.sys` 1.0.0.76: `64B72EF9CE409BB17D31096C119B68C4A6A02AB35B7535D763157A4E1A587E59`, byte-identical to the reviewed 02E0 common driver.
- The keyboard INF matches `HID\VID_1532&PID_02C6&MI_01&COL01` and installs `RzDev_02c6` as its upper filter. The mouse INF creates the control child from `MI_02`; `RZCONTROL\VID_1532&PID_02C6&MI_00` runs `RzCommon` in the same model container.

### Filter interception and output

All offsets below are RVAs in the pinned binary.

The keyboard CONNECT branch (`0xB0203`, at `0x3514`) calls `0x2078`, saves the original class callback in the physical device context, and installs wrapper `0x2430`. That wrapper resolves the device's handler `0x36C0`, which forwards ordinary keyboard packets through the device-local callback pair at `+0x568/+0x570`.

Control callback registration at `0x1630` installs keyboard wrapper `0x1B10` and returns original-output wrapper `0x1E10`. The former reaches input dispatch `0x19B0`; the latter reaches `0x1DD0`, which invokes the saved original class callback without re-entering the hook. Null registration restores the original callback pair. Device association at `0x1198` checks the identity structure before connecting callback owners. CONNECT, wrapper and output functions match their 02E0 counterparts after address normalization; the forwarding handler retains the same ordinary-keyboard and auxiliary branches.

The shared common driver registers its device context and callback at `0x5104/0x517A`. IOCTL wrapper `0x1A10` uses the request queue's WDF-device context, dispatches at `0x2E6C`, and owns that device's hook table at `+0x570`. The exact container, keyboard collection and ancestry checks therefore remain necessary.

### Shared packets and lifecycle

The byte-identical common driver implements the existing codecs: notification `0x88883018` returns `0x130` bytes; output `0x88883020` takes `0x20`; set-hook `0x88883024` takes `0x124`; clear-hook `0x8888302C` takes `0x20`; enumeration `0x88883030` takes a four-byte index and returns `0x124`; enable `0x88883034` takes four bytes. Canonical A/D scan positions remain `0x1E/0x20`.

Common input handler `0x3BBC` consumes matching subscribed events; unmatched input reaches the original callback through `0x4734`. Typed output also calls that original path. Notification subscription is tied to the request file object (`0x374D`, `0x5A08`, aggregate recomputation `0x5CD4`). All existing ownership and cleanup rules transfer: close the subscribed file; remove only independently confirmed canonical owned hooks; preserve unrelated hooks; avoid aggregate disable/reset; stop after uncertain output and retain the recovery journal. Closing a subscription does not prove that previously emitted output was released.

### Product 710 middleware

The exact-product [middleware entry point](https://apps.razer.com/synapse/products/710/mw/index.html) reported build `2609300314`, Git revision `5d7f793ac59d077baa74a1e05c93c177f4f96701`, and Blade middleware dependency `379be0f80fa616514bd450dd2e0285a02b0b9f8b`.

- `main.d7c1244bf00ff7462e7c.js`, SHA-256 `201541BA7E521BB4B161CFCD077BC95EA2577BFFC37B3452D1CCBFB57A57F626`, defines product 710, vendor 5426 and the default disabled A/D pair.
- `6737.0a09be13ed46538a373d.js`, SHA-256 `9D0E95894BE265652412EA84B1B52713A825D083BA0B65C6D9B91C70F659691C`, calls `addUsbDevice` with the default `skipFilterDriver=false`, attaches `snaptapId` to paired down/up mappings, and submits them under `synapse_<productId>_<container>`.

This establishes the filter-backed software mapping route for 02C6. It does not claim that vendor JavaScript or native code was executed during this review.

## Other supported models

Exact vendor manifests, signed installer packages, filter hashes, topology and product-specific SnapTap mappings for RZ09-0483, RZ09-0482, RZ09-0530, RZ09-0328 and RZ09-0484 are pinned in the [vendor artifact review](snap-tap-vendor-artifacts.md). The independent [filter ABI comparison](snap-tap-filter-abi.md) covers versions 1.0.0.71, 1.0.0.73 and 1.0.0.78.

All five expose the same version-1 query interface, size `0x28`, GUID `{14CC0596-CFA5-4379-A5AB-FA24E681D040}`, keyboard type-2 registration, per-device association, original-output bypass and callback restoration. The three .73 filters share executable sections; 02C5's reviewed functions match 02C6 after address normalization; 0255's older compiler plumbing was traced independently. Together with the reviewed common-driver API, this establishes one shared backend for all seven current OpenBlade models.

The 0255 catalogs cover the legacy INF set; their parallel U INF variants are not catalog members. The 2026-10-26 installation review selects only those catalog-covered legacy INFs and their declared co-installer files. The separate setup recipes are documented in the [vendor artifact review](snap-tap-vendor-artifacts.md#complete-installation-recipes). The runtime review alone grants no installer or redistribution authority.

## Physical validation

Only the existing RZ09-0581 observations in [SnapTap](snap-tap.md) are physical results. Newly reviewed profiles remain experimental until their own input attribution, both A/D press orders, stop/restoration and relevant recovery behavior are validated. No other model inherits 0581's physical observations. Each automated installation recipe has its own reviewed inputs; setup on the other six models remains source-backed experimental.
