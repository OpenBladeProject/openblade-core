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
26061. A shared method name alone is insufficient. An exact inherited getter/setter
contract can support experimental admission even when Synapse hides its UI.

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
| Temporary full charge | The exact inherited class defines GET 07/8F and SET 07/0F; shared middleware maps `oneTimeOverride` to bit `0x04`. Product 672 lacks the UI activation flag. | Experimental support requires a valid getter, an enabled charge limit for enablement, other power-mode bits clear, and independent flag and charge-policy readback. |
| Startup animation | Exact inherited class defines getter 0F/98 and setter 0F/18. The missing startupAnimationControlFWVersion prevents Synapse UI initialization, not the command definitions. | Experimental control is admitted only after a valid exact-device getter; changes require fresh baseline and independent readback. |
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
difference is limited to signing data. Neither the version nor the hash
establishes the missing API contract. Its scoped lower-filter and consumer-control
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
CPU/GPU target registers are independently addressed by the exact class and
runner. Host curves in Balanced now preserve both requests; physical RPM
independence remains unverified. The ordinary fixed-speed slider still sets both
targets to one value. Missing temperature data holds the admitted maximum.
Temperature telemetry remains unverified on the physical Blade 18.

The Custom 1700–4300 RPM declaration is unused without the absent
customModeSupportsMoreThanAuto feature. Synapse chooses Automatic on Custom
entry. OpenBlade confirms that handoff before selecting Custom and preserves
the saved manual setting for return to an admitted preset. This is host policy,
not evidence that firmware rejects every manual request. No corresponding
Performance manual range was recovered.

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
missing path. The recorded Observer version collected WMI without the PDH counters
OpenBlade reads. [Observer PR #21](https://github.com/OpenBladeProject/synapse-observer/pull/21)
adds a bounded, read-only check of both known TZRZ paths, recording API and
counter status, units and measured values separately from WMI. A local
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

- Temporary full charge: the inherited command contract supports an experimental
  path despite the absent UI activation flag. Read admission and whole-mask guards
  prevent an unknown flag or another power-mode option from being overwritten.
  Physical charging behavior remains unverified.
- Startup animation (startup lighting): the missing UI feature flag is not a
  firmware prohibition. The experimental backend uses the inherited exact-model
  command definitions, admits the control after a valid getter, and verifies
  every change by another getter.
- Function-key-primary mode: Synapse's host mapping behavior is established.
  OpenBlade still needs per-device suppression before it can safely invert
  this laptop's function row without swallowing an external keyboard's F keys.
- Independent fan targets: source defines separately addressed getters/setters
  for both zones. Synapse supplies one scalar, but OpenBlade now preserves
  independent target requests experimentally within the existing preset ranges.
  Target-register readback is not evidence of independently measured fan RPM.
- Custom/Performance fixed fans: the absent Custom manual feature forces
  Automatic; the declared Custom range alone does not activate it. Performance
  has no recovered manual range. Balanced and Silent remain admitted.

## Startup animation command contract

Product 672 inherits the [pinned rzDevice25 class](https://apps.razer.com/synapse/products/672/mw/rzDevice25.85ce8530211c29f3c304.js),
SHA-256 DFB3E3612D36B20910AF02E61A17ED64A1FFD3781E09C63BAAEE7CBEFDEBCA24. Its GET definition is length 1, class 0F, ID 98, arguments [0].
Its SET definition is length 2, class 0F, ID 18, arguments [profile=0, disable].
The exact middleware calls enabled ? 0 : 1. Readback accepts only [disable] or
[0,disable], with disable 0 or 1; 0 means enabled. Other shapes remain unavailable.

The factory probes only after unique exact-interface admission. A valid getter
establishes capability; a first unavailable or malformed getter leaves unrelated
controls available. A temporary later read failure does not erase established
support, but mutation still requires a new valid baseline. SET success need not
include an echo, so only the independent GET confirms the requested state.
Mismatch or ambiguous submission pauses experimental writes without replay.
Recovery also reads startup state after a possibly submitted startup write;
unrelated thermal readback cannot establish its outcome. No other model inherits
this admission.

## Temporary full charge command contract

The pinned rzDevice25 class defines the one-byte GET tuple [1,7,143] and SET
tuple [1,7,15], corresponding to 07/8F and 07/0F. Its decoder names bit 0x04
oneTimeOverride. Shared chunk 6737 maps the Boolean request to 4 or 0 and
updates that field in the common mask. The getter/setter definitions occur at
character offsets 52774 and 57898; inherited methods at 140931 and 141017.
The shared Boolean mapping and mask helper occur at 1648566 and 1891171.
These locations identify the pinned source bytes, not a future served version.

OpenBlade admits the exact 02A0 capability after a valid getter. Every change
requires fresh flag and charge-policy baselines. Since the setter replaces the
whole mask, other bits must be clear; it writes only 04 or 00. Independent
readback must confirm both the requested flag and unchanged charge policy.
An ambiguous result pauses writes, and recovery rereads both fields without
replaying the setter. Other models do not inherit this admission.

## Temporary full charge in the Observer recording

The October 17 bundle marks battery.temporaryFullCharge and its query as
NotInvestigated. It contains three 07/8F [00] query requests and three
07/0F [00] writes, no 07/0F [04] enable request, and no nonzero Power Mode
Control setter. These occurred during Custom/Maximum Fans off to Balanced
3000 RPM and back. Returned flag bodies were withheld, so the bundle does
not establish full-charge state, enablement, or restoration. The shared SDK
command contract is the evidence for experimental support; the recording is
not evidence of enablement or physical charging behavior.

## Device-specific function-row suppression

Microsoft's [low-level hook structure](https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-kbdllhookstruct)
has no source-device handle. [Raw Input registration](https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-rawinputdevice)
can identify input but its legacy-message suppression does not implement a
system-wide filter for one physical keyboard. Correlation alone cannot make
media-primary inversion safe for external keyboards.

The preferred next investigation is the public interception interface of the
exact installed signed PID02A0 keyboard filter and control child, deriving its
ABI without loading Synapse DLLs. Its internal callbacks are not that ABI.
If it cannot provide the needed path, a new signed device filter would be
required; OpenBlade does not add one. Existing Fn shortcuts, whose actions
wait for matching device evidence, remain separate from always-on inversion.
