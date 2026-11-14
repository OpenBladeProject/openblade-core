# Fan control by power source

This note records the source evidence for the fan controls that OpenBlade may
offer on USB-C or another confirmed limited external adapter, and on battery.
It does not admit a new model or bypass the exact-device, power, competing-
controller, write, and readback gates.

## Policy supported by the vendor source

Host curves are OpenBlade software control over already admitted typed targets;
a generic vendor helper alone does not establish an exact model's capability.
Within an exact model's existing fan capabilities, the source supports this
power policy:

| Power source | Automatic | Fixed target | Smart curve | Maximum Fans |
| --- | --- | --- | --- | --- |
| Full external power | Yes | Yes where separately admitted | Production backends and the exact source-reviewed presets in the [fan review](source-reviewed-thermal-967.md) | Only where separately admitted |
| Confirmed limited external power, including classified USB-C | Yes | Production backends only | Production backends only | Only where separately admitted |
| Battery | Yes | Only the exact models and battery presets listed below and in the fan review | No | No separate Maximum Fans switch |

`FanMode.Maximum` is OpenBlade's legacy name for the validated backend's fixed
maximum-RPM target. It follows the Fixed column. The distinct experimental
Custom-mode Maximum Fans switch remains limited to models that separately
admit it.

The vendor bundles do not identify a connector. They compare the adapter's
reported and recommended wattage, so "USB-C" in OpenBlade still depends on its
own exact-model power classification. An unknown adapter reading must continue
to block a thermal write.

Each pinned policy bundle resolves a non-Custom performance/fan pair through
`getPerformanceFanModeProfile`. The production RZ09-0528 and RZ09-0581 bundles
send Balanced AC on limited external power while preserving the resolved fan
flag. The fixed-capable experimental RZ09-0483 and RZ09-0482 bundles instead
send a literal Automatic flag (`0`) before the speed command. Their
`setThermalFanSpeed` wrapper sends only class, fan ID, and speed; it does not
silently enter manual mode. Experimental USB-C fixed control therefore remains
blocked until separate exact-model evidence establishes that sequence.

For the production RZ09-0528/RZ09-0581 battery paths, the shared policy changes
only a resolved Balanced AC selector to
Balanced DC and preserves the same fan flag; every other selector is sent
unchanged. When the requested fan type is `manual`, it then calls
`setThermalFanSpeed`.

That makes both battery fixed forms source-backed on those two production profiles:

| Battery profile | Automatic | Fixed target |
| --- | --- | --- |
| Battery Saver | selector/flag `03/00` | selector/flag `03/01`, then the speed command |
| Battery Balanced | selector/flag `06/00` | selector/flag `06/01`, then the speed command |

There is no Battery Saver branch that removes or clears the manual flag. Only
Balanced AC is substituted with Balanced DC, again without changing that flag.
These two fixed forms are vendor-source semantics. Battery Saver manual control
was subsequently exercised on RZ09-0581 in the bounded
[low-target round trips](battery-saver-low-fan-validation.md); that result does
not establish another model's physical behavior or Battery Balanced operation.

The developer Battery Saver trial retained a requested 1,500 RPM target and
later measured zero RPM, but its short 2,000 RPM preparation had no held control
or reversal. That association does not establish a causal or reliable fan-stop
command. A literal-zero target was not tested in Battery Saver. These observations
alone establish no fan-stop command or production admission. Current fan-stop
controls follow their separate reviewed contracts and model limits; see
[model and EC coverage](fan-stop-model-coverage.md) and the
[corrected evidence assessment](battery-saver-low-fan-validation.md#withheld-production-capability).
Ordinary fixed-target ranges and direct manual transitions retain their own admission.

Smart curves are external-power-only. The vendor policy stores and reads smart
curve state under `performance.plugged` and starts its temperature monitor only
while `isOnBatteryPower` is false. Its battery settings contain Battery Saver,
Battery Balanced, and `manualFanSpeed`, but no battery curve. OpenBlade admits
host curves on the exact full-power AC presets in the
[complete fan review](source-reviewed-thermal-967.md). This includes all already
admitted ordinary AC Fixed presets on RZ09-0482/RZ09-0483, excluding HyperBoost,
and Balanced/Silent curves on RZ09-0484. Missing CPU
temperature holds the current preset's admitted maximum. Source-reviewed curves
have software failure/recovery evidence; their physical behavior is not inferred
from another model's tests. No source-reviewed curve is admitted on battery or
limited external power.

## Exact model scope

Shared vendor policy is evidence only after OpenBlade's exact model has admitted
the underlying fan command. The table below details the seven original profiles;
all 22 profiles' current preset and range coverage is in the
[complete fan review](source-reviewed-thermal-967.md).

| Exact Windows product | USB identity | Synapse product | USB-C / limited external | Battery | Basis and exception |
| --- | --- | --- | --- | --- | --- |
| `Blade 16 - RZ09-0581` | `1532:02E0` | `736` | Automatic, fixed, smart curve | Automatic, fixed in Saver or Balanced | Production fan backend already admits automatic, fixed, and curve. The preserved observer run physically covered AC fixed only. |
| `Blade 16 - RZ09-0528` | `1532:02C6` | `710` | Automatic, fixed, smart curve | Automatic, fixed in Saver or Balanced | Production fan backend admits automatic, fixed, and curve. An older Synapse UI observation showed fixed unavailable on battery and USB-C, so fresh physical validation remains required before treating the newer source behavior as physically confirmed. |
| `Blade 16 - RZ09-0483` | `1532:029F` | `671` | Automatic only | Automatic, fixed, 2,000–5,000 RPM | Existing Fixed scope is unchanged. Balanced/Silent/Performance/Custom host curves use 2,000–5,000 RPM on confirmed full-power AC only; the lower bound is measured by the upstream owner, not an OpenBlade curve validation. Limited-power Fixed and curves remain blocked. |
| `Blade 14 - RZ09-0482` | `1532:029D` | `669` | Automatic only | Automatic, fixed, 2,200–5,000 RPM | Existing Fixed scope is unchanged. Balanced/Silent/Custom host curves use the separately source-extrapolated 2,200–5,000 RPM range on confirmed full-power AC only; the floor is not physically validated. Limited-power Fixed and curves remain blocked. |
| `Blade 14 - RZ09-0530` | `1532:02C5` | `709` | Automatic only | Automatic; fixed in Battery Balanced, 3,400–5,200 RPM with fresh metadata | Full-power AC admits fixed targets and host curves in its reviewed Balanced, Silent, Maximum Power and Custom ranges. Battery Saver remains Automatic. See the complete fan review. |
| `Blade 15 Base Model (Early 2020) - RZ09-0328` | `1532:0255` | `597` | Automatic only | Automatic only | Fixed targets and host curves require confirmed full-power AC and the reviewed Balanced, Performance (Gaming) or Creator range, intersected with fresh metadata. Existing upstream Silent/Custom controls do not establish a manual range there. |
| `Blade 18 - RZ09-0484` | `1532:02A0` | `672` | Automatic only | Automatic, shared fixed target in Balanced, 2,200-3,800 RPM | Exact Balanced setters and a rising fan-1 RPM stream are observed. The pinned class-1 stored-target getter is source-backed and must succeed before writes. The earlier ignored-RPM report concerned HyperBoost. Silent also admits linked 3,700-5,300 RPM targets on full external power. Balanced/Silent host curves preserve separate target requests and hold the active preset maximum without temperature data; variable curves and physical RPM independence remain unverified. Custom and Performance manual fans remain unavailable. See [Observer evidence](blade18-observer-evidence.md#follow-up-bundle-2026-10-17). |

The source-reviewed policy retains battery Fixed on RZ09-0483/RZ09-0482 and
Balanced-only linked fixed targets on RZ09-0484. RZ09-0530/RZ09-0529 additionally
admit fixed targets in Battery Balanced with fresh metadata. The other dynamic
profiles do not gain battery manual targets. Source-reviewed curves require
confirmed full-power AC and an admitted curve preset. RZ09-0482/RZ09-0483 reuse
their ordinary AC Fixed preset scope, excluding HyperBoost; no Balanced reset or
Automatic bridge is required. The two validated production backends retain their separate
limited-power curve capability. Exact-device, power, ownership, per-write and
readback gates remain required throughout.

## Pinned vendor bytes

The official Synapse files below were inspected as data on 2026-10-15; no
vendor code was executed. The main bundle identifies the exact product and its
feature configuration. The policy bundle contains the shared fan/power apply
path described above. Search anchors in the minified policy files are
`getPerformanceFanModeProfile`, `setThermalFanMode`,
`setThermalFanSpeed`, `isBalancedDCSupported`, and `smartFanCurve`.

| Product | Main bundle SHA-256 | Policy bundle SHA-256 |
| --- | --- | --- |
| `710` | `201541BA7E521BB4B161CFCD077BC95EA2577BFFC37B3452D1CCBFB57A57F626` | `9D0E95894BE265652412EA84B1B52713A825D083BA0B65C6D9B91C70F659691C` |
| `736` | `A699B71BF0E825B733C8EB5E681F91E85538DBB596D68D8A26BA5F7E8CB3B043` | `CD29E8D89135EA967AB13B4607A6524087A6B1DDBF76F7C73A39C7E4C6EC404D` |
| `671` | `CACF8758BF8B4DF35B7D51536830149ED428960AFEDF08CC81367E75520551D1` | `54D3A9BBEC83568B02342AA54FF47A94EE55A48ECA85B5D9F28D60D01A0B1A9A` |
| `669` | `EEB8A171E77D1E066655D7FC997E4015B420B0F936F2464A27A87FDAFB5AC302` | `D1AC2212AED5491D3EAB69ABAFA270B963A2336E859DF6BFBF9ED9A455E3791B` |
| `709` | `CC8E0EFD2B37F1ED5AF6C8CC8C3AB1327B9502927DC05F20EE7465486AB006F1` | `4758FB7E2D8296564FB9CE971F6DCE3D580ED5407EEF07A69E1C362A04FF28C0` |
| `597` | `A864DC2BE2D35BC3703B81476C1659CAAA4D2C144C89616D9D5D0EE398F030FB` | `C6E6E76DFB61F960913E22B864A31273280547E21E1462DE3138F096D2DB56EA` |
| `672` | `D523F5080784B07CB1888C39E636004626E073E2967B85999498030102F85FAE` | `651F63DCC0EFC92B0FD7496EE70751707B5F650F20AF5C01C3255B61817C1997` |

The full official URLs, byte sizes, base-device hashes, and adapter command
provenance are recorded in [Synapse adapter wattage evidence](synapse-adapter-wattage.md#pinned-vendor-files).

## Preserved physical and observer evidence

The supplied observer artifacts are retained by filename and hash so their
limits remain explicit. They contain no user path in this document.

| Artifact | SHA-256 | What it establishes |
| --- | --- | --- |
| `final-summary.json` | `E8D3FA74041CBEF8CF47748C9EB5C6BA05CDAACEF52C8FAFF6ED09F88D4B0124` | Lines 133-154 summarize an RZ09-0581 AC user-reported `Automatic -> Fixed 5400 RPM -> Automatic` sequence. Lines 274-280 state that there was no firmware readback, setter validation, or production admission. |
| `live-workflows-6c61570-final.zip` | `EFB2100C372ABF2A5BC6CC40F1F6F82342E189D1075B42B43A6DAC68B973CEB0` | `manifest.json` lines 3040-3073 contain the AC fan markers. Line 30 says the markers are user-reported and the calls are user-mode evidence. `coverage-draft.json` lines 266-303 leave automatic, fixed bounds, curve writes/readback, and RPM query `NotInvestigated`; lines 306-313 capture only reported fixed-speed evidence. |
| `live-battery-78c9395.zip` | `3035F2B04BA6A0C228655E193FB1E8856DB948CA26B73025F07B20C44804A92A` | `manifest.json` lines 2581-2614 contain an RZ09-0581 battery `Balanced -> Battery Saver -> Balanced` performance sequence. `coverage-draft.json` lines 266-303 leave all fan operations `NotInvestigated`. |
| `live-battery-summary.json` | `9C037312262615826E61F3A1852845D82E2A0EA738836DD30F563C77C2908938` | Lines 20-24 identify battery context and explicitly say `firmwareReadback: false`. |

These artifacts support the AC fixed workflow and the availability of both
battery performance profiles. They do not physically validate fixed fans on
battery or limited external power, smart curves on limited external power, or
any of the corresponding firmware selector/flag pairs.

## Checked-in source references

- `src/OpenBlade.Protocol/ExperimentalBladeModels.cs` retains per-feature source
  evidence and each exact model's Synapse product linkage.
- `src/OpenBlade.Protocol/ExperimentalBladePort.Fans.cs` defines fixed ranges,
  separate static host-curve admission and typed target/readback behavior.
- `src/OpenBlade.Protocol/ExperimentalBladePort.DynamicFanRange.cs` intersects
  reviewed intent with fresh current-preset metadata on the 17 dynamic profiles.
- `src/OpenBlade.Service/FanCurveControl.cs` and `ServiceControlRuntime.cs` bound
  host maintenance to its admitted preset, power source and exact owned intent.
- [The complete fan review](source-reviewed-thermal-967.md) records current
  ranges and limitations. Its separate legacy-curve addendum pins the existing
  671/669 helpers and upstream range provenance without changing historical ledgers.
- `docs/rz09-0528-support-implementation-log-archive.md` retains the older
  battery/USB-C UI observation and its physical-validation boundary.

## Component compatibility

`SupportsManualFansOnLimitedPower` is advertised only by the two exact production
backends. Missing capability data defaults to false. The strict JSON codec
rejects unknown members. This field was introduced in service IPC 66 and requires
a coordinated component update; it is omitted from the protocol-51 read-only projection.
Session Agent IPC and persisted profile schemas are unchanged.
