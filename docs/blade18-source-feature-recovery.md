# Blade 18 source feature recovery

This investigation covers Razer Blade 18 RZ09-0484, USB `1532:02A0`, and
Synapse product 672. Sources were inspected on 2026-10-18. It identifies
which features have an exact-model implementation path and which still lack
one. The work used static source inspection, HTTPS downloads, archive
extraction, signature checks, and the existing sanitized Observer inventory.
No vendor code, installer, driver operation, input injection, or hardware
write was executed.

## Pinned vendor sources

| Source | SHA-256 |
| --- | --- |
| [Installer index](https://apps.razer.com/synapse/products/672/mw/installer-manifest.json) | `0F47B8733DFEDABA3A2C85F5DB9D1914764E1B7D5B5ED1084AD3D5C422A4CDA7` |
| [Installer resources 1.0.1](https://apps.razer.com/synapse/products/672/mw/manifests/manifest-1.0.1.json) | `DDB1E55CDA72949AE77D38CBF65EF31BE813913026427D7FB2CEA6A3DA6E4FB2` |
| [Product-672 middleware entry](https://apps.razer.com/synapse/products/672/mw/main.57245df0507455a5119e.js) | `D523F5080784B07CB1888C39E636004626E073E2967B85999498030102F85FAE` |
| [Shared middleware chunk served by product 672](https://apps.razer.com/synapse/products/672/mw/6737.e50bb836b878190315d7.js) | `651F63DCC0EFC92B0FD7496EE70751707B5F650F20AF5C01C3255B61817C1997` |

The middleware entry contains feature injection and defaults in modules
37915 and 68432, exact-product injection and `deviceUI` in module 70400,
exact-product `runtimeData` in module 35924, and profile defaults in module
26061. Shared method names alone do not establish that product 672 enables
their firmware paths.

## Installer manifests do not enable features

`useMWManifest` selects the installer-resource lookup. Shared module 37574
checks the `razer-installer-manifest-aio` cache, requests
`installer-manifest.json`, and selects its versioned resource document. It
can retain a newer cached index and use the corresponding cached resource
manifest when a network request fails. This path does not modify
`featuresList`.

The recovered index selects resource version 1.0.1. Its six resources are
PID02A0 driver 0.1.1.0, BladeDLL 1.0.15.1, two Intel overclocking DLLs
1.0.21.0, RazerIntelOverClock 1.0.2.0, and RazerXTU 1.0.5.3. It supplies no
THX package or additional firmware feature flags.

## Firmware and host features

| Feature | Exact-product evidence | Result |
| --- | --- | --- |
| Full charge | Shared middleware has the `oneTimeOverride` bit `0x04`, but product 672's `deviceUI.power` lacks `hasBatteryChargingOverrideEnabled`. | No new firmware admission. The shared method and bit definition are insufficient. |
| Startup animation | Shared code checks `startupAnimationControlFWVersion`; product 672's runtime data and feature injection supply no value. | No new firmware admission. |
| Fn primary mode | The profile defaults to `functionKeyPrimary=func`, and shared function `An` rebuilds host mappings. Firmware `setFunctionKeySwap` requires `useFunctionKeySwap.config.primaryKey`; `setFnKeyState` requires `functionLighting.config.functionKey`. Both gates are absent for product 672. | Host mapping behavior is source-backed; a firmware primary-key switch is not admitted. |
| Gaming Mode | Product 672 has profile defaults and a host mapping generator. Firmware `setGameModeState` requires `functionLighting.config.gamingMode`, which is absent. | OpenBlade can reuse its existing opt-in host Windows-key and Alt+Tab policy without claiming firmware Gaming Mode readback. |
| HyperBoost | The shared `isHyperBoostSystem` path exists, but product 672 supplies no enabling feature flag. | No new firmware admission. |

For the pinned shared chunk, Fn mapping function `An` starts at character
offset 1458187, and the optional firmware Gaming Mode function `ii` starts
at character offset 1626147. Module 51765's `de` builds the host app-engine
profile. These locations describe the pinned bytes above, not future
versions served at the same product URL.

Media-primary behavior needs a per-device input path before it can be
exposed. OpenBlade's present low-level hook can suppress an F key before
Raw Input establishes its originating keyboard. Reusing that hook to
remap only this laptop could therefore swallow an external keyboard's
unmodified F keys. The existing global opt-in Gaming Mode policy has a
different, explicit scope.

## SnapTap driver evidence

The resource manifest provides the exact
[PID02A0 driver installer 0.1.1.0](https://app-assets.razer.com/files/synapse/devices/672/1.0.0.0/PID02a0Drv_v0.1.1.0.exe).
Its 1,107,592-byte payload matches SHA-256
`1905D41BADC81778034BB25EC1C79455908175D264E3ED5E077175698B4E5176`
and has a valid Razer USA Ltd. Authenticode signature. It was unpacked as
data without running its installation commands.

| Extracted component | Version or role | SHA-256 |
| --- | --- | --- |
| `RzDev_02a0.sys` | 1.0.0.73; valid Razer signature | `AD50A47698F0FA882B0A44F5CC898A1A8A1B8D9213BFDFF629CF49F63872D180` |
| `RzDevU_02a0_Kbd.inf` | Keyboard package, DriverVer 09/09/2022,10.0.15063.0 | `E53371B04D31FF201EB907E9E597E3827D4DF474A2B4588440A46B149BA9544B` |
| `rzdev_02a0_kbd.cat` | Keyboard catalog; valid signature | `0D4FCDD203A91C69EE7B0B1CDFB56BC4C1B04C25A057DF45B000DE6EF89D9F48` |

The keyboard INF binds `HID\VID_1532&PID_02A0&MI_01&COL01` and installs
`RzDev_02a0` as its upper filter. The mouse INF binds
`HID\VID_1532&PID_02A0&MI_02` and sets `ControlDevice`. The binary contains
the `RAZER\DeviceContol` child hardware ID and the
`RZCONTROL\VID_%04X&PID_%04X&MI_%02X` instance-name format. The reviewed
shared RzCommon INF binds that generic child hardware ID. This establishes
a plausible model-specific control path, but does not complete the
keyboard interception and output contract.

The existing Observer inventory records a trusted installed
`RzDev_02a0.sys` with the same 1.0.0.73 version and a different SHA-256:
`B77F49B11E4012C25A2815791684BE0A9F8DF0CE090797435FE2744CB6DE27A5`.
The inventory contains no binary with which to determine whether that
difference is limited to signing data. A version match cannot substitute
for a reviewed content pin. Its scoped lower-filter and consumer-control
records also do not establish complete keyboard and RzCommon topology.

Static disassembly shows a different internal callback/context
implementation from the reviewed 02E0 filter 1.0.0.78. The 02A0 dispatcher
at RVA `0x1A20` dereferences `context+0x28`; the 02E0 dispatcher at RVA
`0x3250` uses its passed context directly. Both recognize internal
operations `0x88883010`, `0x88883014`, and `0x88883140`. These are distinct
from the public SnapTap hook operations implemented by RzCommon. This
comparison does not prove ABI compatibility or incompatibility.

SnapTap remains unavailable pending review of the exact 02A0 keyboard
suppression/output callbacks and their interface with the pinned RzCommon
build. The existing [SnapTap backend](snap-tap.md) must retain its exact
host, collection, container, component-hash, ownership, and recovery checks.
No package installation or replacement is implied by this investigation.

## THX uses an older control integration

The [official RZ09-0484 master guide](https://dl.razerzone.com/master-guides/RazerSynapse/Blade18-00000672-en.pdf)
identifies its speakers as powered by THX Spatial Audio.
[Razer's THX support article](https://mysupport.razer.com/app/answers/detail/a_id/3779)
lists this model and directs users to the Microsoft Store THX Spatial
Audio Control app; it places controls for 2024-onward models in Synapse.
[Razer's setup guidance](https://mysupport.razer.com/app/answers/detail/a_id/5798)
also distinguishes 2023-and-older Blade models. Absence of THX resources in
the product-672 Synapse manifest therefore does not mean that this laptop
lacks THX audio.

OpenBlade's current [THX integration](thx-audio.md) admits specific
`rznb11` and `rznb12` component builds. No exact RZ09-0484 legacy APO,
codec binding, client ABI, or complete component pins were recovered here.
The existing Observer bundle does not provide that audio-stack inventory.
Neither the shared THX client API nor another Blade's package authorizes
adding this model to those profiles.

The remaining SnapTap and THX questions concern native components and
source contracts. Another USB capture would not establish those missing
contracts. Static findings do not establish physical input enforcement,
audible audio processing, independent setting readback, or restoration.

## Fan controls

Product 672 main module 26061 declares Balanced 2200–3800 RPM and Silent
3700–5300 RPM. Shared module 85225 applies the single manualFanSpeed scalar
to both zones after selecting the preset's raw mode (Balanced 0, Silent 5).
Both use class-1 target registers and the existing typed readback path. Silent
fixed fans are admitted experimentally with their own range. Independent
CPU/GPU targets are not exposed; host curves in Balanced link both fan requests
to the higher target. Missing temperature data holds the admitted maximum.
Temperature telemetry remains unverified on the physical Blade 18.

The Custom 1700–4300 RPM declaration is unused without the absent
customModeSupportsMoreThanAuto feature. It does not admit Custom manual fans,
and no corresponding Performance manual range was recovered.

## Temperature-source recovery

OpenBlade's existing CPU reader uses Windows PDH High Precision Temperature
counters for the Razer EC thermal zone named TZRZ. It tries both known ACPI
paths on all admitted models, including Blade 18; the paths are not gated to
the two laptops on which they were validated. NVIDIA temperature uses NVML
only while the GPU is known to be active. Neither reader loads a Synapse DLL.

The reviewed Product 672 sources did not supply another Windows CPU-temperature
provider or an exact Blade 18 ACPI-to-CPU mapping. The existing Observer bundle
reported an available root/wmi MSAcpi_ThermalZoneTemperature query with zero
rows. That does not prove that PDH has no usable counter, but it supplies no
missing path. Observer currently collects this WMI query, not the PDH counters
OpenBlade reads. Its absent WMI result therefore leaves PDH availability unknown.
A useful future Observer improvement is a bounded, read-only check of the known
TZRZ counter paths, including success/failure, units and measured value. A local
read-only temperature check can establish availability without a USB capture or
fan write. Another capture of fan commands would not establish that path.

Shared middleware monitoring selects measured Temperatures.Package current,
minimum and maximum values. Its fixed Temperature:100 chart limit is not a
reading. This monitoring path requires hasSystemMonitoringStatistics and
systemHelper configuration; Product 672 supplies neither. The recovered native
chain is RzSystemCommon to RzSystemService to CPUID SDK, whose binary contains
CPUZ driver images. Those installed common components are architectural evidence,
not proof of a provider enabled for Product 672. The exact product's BladeDLL
has no recovered CPU-temperature export; its Intel overclocking stack requires
XTU service and exposes tuning operations.

The [sanitized recovery report](blade18-temperature-source-recovery.json) pins
the official source URLs, hashes, offsets, component exports and uncertainties.
All inspection was static. No vendor DLL, service or driver was run or added
as a telemetry dependency. Unknown thermal zones, utilization counters and
thermal limits must not be relabeled as CPU package readings.

Consequently, this investigation does not establish working variable fan curves
on Blade 18. The host curve backend remains available in Balanced and holds
3800 RPM if CPU temperature is unavailable. A standalone, measured CPU source
is still needed where neither known Windows counter works.

## Meaning of the remaining restrictions

These are current implementation and source-admission limits, not claims that
the firmware or hardware cannot support the feature.

- Temporary full charge: the shared bit definition exists, but no enabled
  exact-product setter path was recovered. Existing Power Mode Control queries
  now name bit 0x04 in diagnostics alongside CPU Boost and local dimming. This
  adds no query, setter, capability or claim that charging behavior is confirmed.
- Startup animation (startup lighting): inherited get/set definitions exist, but Product 672 omits
  the firmware-version feature configuration that activates them in Synapse.
- Function-key-primary mode: Synapse's host mapping behavior is established.
  OpenBlade still needs per-device suppression before it can safely invert
  this laptop's function row without swallowing an external keyboard's F keys.
- Independent fan targets: separate zone registers exist, but the exact-model
  active task sends one speed to both. Asymmetric operation is an extrapolation
  that has not been established by that source.
- Custom/Performance fixed fans: the absent Custom manual feature forces
  Automatic; the declared Custom range alone does not activate it. Performance
  has no recovered manual range. Balanced and Silent remain admitted.
