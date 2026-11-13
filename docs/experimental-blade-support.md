<a id="experimental-blade-support"></a>

# Source-reviewed Blade support

English | [简体中文](zh-CN/experimental-blade-support.md) | [Русский](ru-RU/experimental-blade-support.md)

These twenty laptop families are officially supported using reviewed exact-model
Synapse and upstream sources. OpenBlade recognizes them automatically in both
stable and pre-release builds. Physical validation is tracked separately for each
model and feature; source-reviewed support does not claim complete hardware
testing. RZ09-0581 and RZ09-0528 retain their separately physically validated
support. The limits and remaining experimental features below still apply.

Stop/Auto supports eight model families in USB-C Balanced. See the
[model and EC version coverage](fan-stop-model-coverage.md) for the exact list.
The seven additions use separately reviewed firmware contracts and have not
been physically tested. They require the reviewed InpOut driver already
installed; PawnIO supplies CPU temperatures only.

## Lighting coverage

| Models | Lighting controls |
| --- | --- |
| RZ09-0287, RZ09-0314, RZ09-0330, RZ09-0370, RZ09-0409, RZ09-0508 | Keyboard brightness; logo Off, Static and Breathing |
| RZ09-0300, RZ09-0351 | Keyboard brightness |
| RZ09-0301 | Brightness; keyboard Off, Static, Breathing, Spectrum and Wave; logo controls |
| RZ09-0421 | Brightness; keyboard Off, Breathing, Spectrum and Wave |
| RZ09-0423, RZ09-0427 | Brightness; keyboard Off, Static, Breathing, Spectrum and Wave; logo controls |
| RZ09-0510 | Brightness; keyboard Off, Breathing and Wave; logo controls |
| RZ09-0509, RZ09-0529 | Brightness; keyboard Off, Static, Breathing and Wave; logo controls |

The seven MCU-backed additions support random, single-color and dual-color
Breathing, and both Wave directions. RZ09-0301 also retains independent
brightness/logo editing and existing field-only profiles. The added controls
use exact-model source evidence; physical validation remains pending. Existing
lighting backends retain their own admission. See the
[lighting review](source-reviewed-lighting.md) for all 22 profiles, MCU-first
and upstream evidence, frame/input mapping and remaining gaps.

## Available controls

The earlier five models retain the following controls. The additional models
have separate, narrower contracts below.

| Laptop | Model number | Controls |
| --- | --- | --- |
| Blade 14 (2025) | `RZ09-0530` | Balanced, Silent, battery modes, Custom levels, keyboard and logo lighting, 50–100% charge limits, optional [THX audio](thx-audio.md) with a supported installed stack |
| Blade 16 (2023) | `RZ09-0483` | Performance and Custom levels with CPU Boost, HyperBoost with or without the cooling pad, fixed and maximum fans, keyboard and logo lighting, 50–100% charge limits |
| Blade 14 (2023) | `RZ09-0482` | Balanced, Silent, Custom levels, HyperBoost with or without the cooling pad, fixed fans, keyboard and logo lighting, 50–100% charge limits |
| Blade 18 (2023) | `RZ09-0484` | Performance and Custom levels, including every CPU Boost/GPU level combination and High/High, linked fixed fans in Balanced/Silent, fan curves in Balanced and maximum fans, keyboard and logo lighting, 50–100% charge limits |
| Blade 15 Base (Early 2020) | `RZ09-0328` | Gaming and Custom levels, single-zone keyboard lighting |

### Additional 2024/2025 models

| Laptop | Model number | Initial controls |
| --- | --- | --- |
| Blade 18 (2025) | `RZ09-0529` | Balanced, Silent, Turbo, Maximum Power, Battery Saver, Custom Low/Medium/High, Automatic fans, charge limits 50–80% in 5% steps or protection off |
| Blade 14 (2024) | `RZ09-0508` | Balanced, Silent, Turbo, Custom Low/Medium/High with CPU Boost, Automatic fans, Maximum Fans, charge limits 50–80% in 5% steps or protection off |
| Blade 16 (2024) | `RZ09-0510` | Balanced, Silent, Turbo, Custom Low/Medium/High with CPU Boost, Automatic fans, Maximum Fans, charge limits 50–80% in 5% steps or protection off |
| Blade 18 (2024) | `RZ09-0509` | Balanced, Silent, Turbo, Custom Low/Medium/High, Automatic fans, Maximum Fans, charge limits 50–80% in 5% steps or protection off |

All four offer Custom CPU/GPU Low, Medium and High. Only Blade 14 (2024)
and Blade 16 (2024) additionally offer CPU Boost.

All four offer Balanced on battery. The other listed presets require confirmed
full-power AC, except Battery Saver on Blade 18 (2025), which is a battery mode.
Fixed fan speeds, curves, HyperBoost, driver integrations and additional
controls are not admitted on these four models. Their support does not inherit
the earlier models' feature list. If fans are currently manual, choose Automatic
and confirm the handback before changing presets or Custom levels: these profiles
cannot yet confirm and restore manual targets. See the [exact-model admission guide](source-reviewed-blade-models.md)
for identity, source pins, command boundaries, and remaining physical validation.

### Additional 2021/2022 models

| Laptop | Model number | Initial controls |
| --- | --- | --- |
| Blade 14 (2022) | `RZ09-0427` | Balanced, Silent, Custom CPU Low/Medium/High/Boost and GPU Low/Medium/High, Automatic fans, charge limits 50–80% in 5% steps or protection off |
| Blade 15 (2022) | `RZ09-0421` | Balanced, Silent, Custom CPU Low/Medium/High/Boost and GPU Low/Medium/High, Automatic fans, charge limits 50–80% in 5% steps or protection off |
| Blade 17 (2022) | `RZ09-0423` | Balanced, Silent, Custom CPU Low/Medium/High/Boost and GPU Low/Medium/High, Automatic fans, charge limits 50–80% in 5% steps or protection off |
| Blade 14 (2021) | `RZ09-0370` | Balanced and Custom CPU Low/Medium/High/Boost and GPU Low/Medium/High on full-power AC, Automatic fans; no charge-limit controls |

Charge limits require the reviewed firmware updates for the 2022 models and a
successful fresh charge-state read before a change. See the linked review for Razer firmware
updates; OpenBlade does not update firmware automatically.

The three 2022 models also offer Balanced on battery; Silent and Custom require
confirmed full-power AC. The 2021 model's admitted thermal controls require AC.
These four models do not admit Turbo, Maximum Fans, fixed fans, fan curves,
HyperBoost or driver/THX integrations. If the current fan mode is
manual, explicitly select Automatic and confirm handback before changing
performance. Physical validation remains pending. See the
[2021/2022 source review](source-reviewed-blades-2021-2022.md) for exact identities
and evidence.

### Additional 2019/2020 models

| Laptop | Model number | Initial controls |
| --- | --- | --- |
| [Blade 15 Advanced (Mid 2019)](source-reviewed-rz09-0301.md) | `RZ09-0301` | Balanced/Custom CPU/GPU levels; Balanced fixed fans and curves on full-power AC; brightness and logo |
| Blade 15 Base (2019) | `RZ09-0300` | Balanced, Gaming and Creator on full-power AC, Automatic fans |
| Blade 15 Advanced (Early 2020) | `RZ09-0330` | Balanced and Custom CPU/GPU Low/Medium/High on full-power AC, Automatic fans |
| Blade 15 Base (Late 2020) | `RZ09-0351` | Balanced, Gaming and Creator on full-power AC, Automatic fans |

These three models offer Balanced with Automatic fans on battery. AC performance
changes require a fresh successful adapter-power read and agreement with Windows
power status. If fans are manual, explicitly select Automatic and confirm
handback before changing performance. Fixed fans, curves, charge limits, Silent,
Maximum Fans, HyperBoost and driver/THX integrations remain unavailable.
The Advanced model's CPU Boost requires an EC prerequisite that OpenBlade cannot
yet verify, so its CPU choices stop at High. Gaming on the 2019 Base selects the
reviewed HID preset; it does not apply Synapse's additional native GPU overclock.
Physical validation remains pending. See the
[2019/2020 source review](source-reviewed-blades-2019-2020.md) for evidence and
reviewed candidates that still lack an exact Windows identity.

### Blade 15 Advanced Mid 2021 and Blade Pro 17 2019

| Laptop | Model | Available controls |
| --- | --- | --- |
| Blade 15 Advanced (Mid 2021) | `RZ09-0409` | Balanced/Custom on full-power AC; CPU Low/Medium/High/Boost, GPU Low/Medium/High; Automatic fans |
| Blade Pro 17 (2019) | `RZ09-0287` | Balanced/Custom on full-power AC; CPU/GPU Low/Medium/High; Automatic fans |
| Blade Pro 17 (Late 2019) | `RZ09-0314` | Balanced/Custom on full-power AC; CPU/GPU Low/Medium/High; Automatic fans |

If fans are currently manual, explicitly select Automatic and confirm the handback
before changing presets or Custom levels: these profiles cannot yet confirm and
restore manual targets. Silent, Turbo, Maximum Fans, fixed fan targets, curves,
charge controls, HyperBoost and driver/THX integrations are not admitted.
Physical validation is pending. See the [exact-model review](source-reviewed-blades-issue-949.md)
for source pins and limits for all three exact profiles.

The following detailed control notes apply to the earlier five models.

Close Synapse before using these controls, or use
[Synapse Blade Blocker](synapse-blade-blocker.md) after checking its model coverage
and applying the patch. OpenBlade checks the adapter's reported wattage before
external-power performance or fan changes. A confirmed limited adapter allows
Balanced and returning fans to Automatic; other external-power thermal changes
wait for confirmed full power. Models with fixed-speed control also offer
Automatic and fixed fans on battery. Blade 18 also offers linked fixed fans
in Silent on full power. Fixed speed on a limited adapter is not
yet supported for these models. If wattage cannot be confirmed,
the change is blocked. The Blade 14 (2025) also offers
Battery Saver and Battery Balanced on battery power.

- Fixed fan targets cover 2,000–5,000 RPM on the Blade 16 (2023), and
  2,200–5,000 RPM on the Blade 14 (2023). The Blade 18 (2023) offers one
  2,200–3,800 RPM control for both fans in Balanced, and 3,700–5,300 RPM in Silent.
  Fan curves run in Balanced and preserve the separate fan target requests. Missing
  temperature readings hold the maximum target. [Source recovery](blade18-source-feature-recovery.md#temperature-source-recovery)
  has not established a standalone Blade 18 CPU-temperature provider; variable
  curves remain unverified where neither known Windows counter works.
- Maximum Fans is a separate Custom-mode switch on the Blade 16 (2023) and
  Blade 18 (2023). Select Automatic fans before turning it on. Maximum Fans cannot
  change while another
  Power Mode Control option is on, such as local dimming or temporary full charge.
  OpenBlade turns Maximum Fans off before leaving Custom.
- Custom CPU/GPU levels are Low, Medium, and High. The Blade 16 (2023) also
  offers CPU Boost and High/High. Blade 18 (2023) offers all twelve CPU/GPU
  combinations, including Boost/Medium and High/High, using Synapse's independent
  level controls. On Blade 18, entering Custom returns fans to Automatic; the
  saved fan setting remains available when returning to an admitted preset.
  Blade 16 (2023) and Blade 14 (2023) preserve readable fixed fans when entering
  Custom or changing CPU/GPU levels.
- RZ09-0328 uses single-zone RGB lighting; Wave and logo controls are not
  supported. The newer models offer brightness, built-in effects, and logo
  lighting. Static RGB on both Blade 14 models, Blade 16 (2023), and Blade 18 (2023) is one color
  across the keyboard. Blade 18 also accepts per-key colors and sequence playback
  from classic Keyboard .chroma imports.
  Firmware idle fade can restore the built-in effect; select Static again to repaint.
  Some firmware returns the Wave effect without its direction. OpenBlade keeps
  the complete lighting setting unavailable and can directly apply another
  built-in effect or logo setting. Wave writes include the requested direction;
  readback confirms only the effect and brightness when direction is omitted.
  Host-rendered effects require a complete, restorable baseline; select a
  readable built-in effect first. See [compact Wave readback](compact-wave-readback.md)
  for source evidence and model coverage.
  Both Blade 14 models, Blade 16 (2023), and Blade 18 (2023) offer whole-keyboard Ambient Awareness
  and Audio Meter.
  Both Blade 14 models and Blade 16 (2023) also offer single- and dual-color
  Breathing, leftward Wave, Fire, Wheel, Starlight and Tidal as
  [software grid effects](blade14-lighting-parity.md). Blade 14 (2025) also offers
  Reactive, Ripple and imported Keyboard .chroma sequences using its
  [firmware-derived key map](blade14-keymap.md). Regional keys whose addresses
  differ between firmware layouts remain unavailable.
  RZ09-0328 offers random or single-color Breathing. Additional Synapse
  effects observed for the Blade 18 are recorded in diagnostics. Blade 18 also
  offers experimental Fire, clockwise or counterclockwise Wheel, and Starlight.
  Fire repeats a short recorded sample; Wheel plays a recorded cycle. Starlight
  uses the shared twinkle pattern over this model's RGB cells. Reactive, Ripple
  and Tidal use [recovered Synapse definitions](blade18-spatial-lighting.md).
  Blade 18 also offers single- and dual-color Breathing and leftward Wave as
  host-rendered patterns. See the same guide for timing and import limits.
  The physical keyboard region selects the matching Synapse map, including
  regional Enter, backslash, Alt, ISO and Japanese keys. If the region cannot be
  read, keys with conflicting regional bindings do not trigger these effects.
- Blade 18 startup animation is available experimentally after its state can be
  read. Each change is checked by a fresh readback.
- Blade 18 Gaming Mode can block the Windows key and Alt+Tab while
  OpenBlade is running. Blocking applies to all keyboards. See the
  [source recovery notes](blade18-source-feature-recovery.md) for remaining gaps.
- Blade 18 (2023) offers the [default Synapse Fn shortcuts](blade18-fn-decoder.md#default-shortcuts):
  performance, refresh rate, touchpad, F1–F12, arrows and Delete. The decoder and
  actions are source-backed; behavior on the laptop still needs testing.
  OpenBlade handles these shortcuts only while the built-in keyboard is the only
  keyboard connected.
- Blade 14 (2023) and Blade 16 (2023) offer HyperBoost on full-power
  AC, with or without the Razer Laptop Cooling Pad. They require MCU firmware
  1.1.1.3 or later on the Blade 14, and 1.5.0.0 or later on the Blade 16.
  On the Blade 14, turn off
  Maximum Fans in Synapse first if it was enabled there; OpenBlade cannot yet
  restore that option on this model.
  Blade 18 (2023) remains observed-only: turn HyperBoost off in Synapse before
  changing performance.
- Charge limits use 1% steps; 100% turns protection off. The Blade 15 does not
  support charge limits. Blade 18 also offers experimental temporary full charge
  after valid state readback. Enabling it requires an active charge limit; other
  Power Mode Control options must be off.

Battery changes on these profiles and the seven newer limiter profiles share a
bounded confirmation window: only GETs returning the exact pre-write raw byte
are retried, up to nine reads 250 ms apart, without repeating the SET. A different
value, invalid or failed read, cancellation or exhausted window pauses writes.
The older profiles retain their required echoed SET acknowledgment; the seven
newer profiles accept an optional returned-state byte. Independent requested-state
readback remains required. This software tolerance does not establish firmware
latency on every model.

## Send a useful test report

The earlier five models also offer [SnapTap](snap-tap.md#supported-driver-profiles) through
the Windows-hook backend or their own reviewed Razer keyboard filters and the
shared control driver. The Razer backend remains experimental on these models.
If the Razer stack is missing, use **Install driver**; OpenBlade checks Windows
Update first and can use the model's pinned package after an initial search finds
no suitable offers. Synapse is not required. SnapTap's physical validation is
separate from these models' firmware controls.

1. Install the complete [current release](https://github.com/OpenBladeProject/openblade-core/releases).
   Note the version and detected model in **About**.
2. Use **About → Export diagnostics** to save a baseline ZIP.
3. Try one setting. Record the power source, starting setting, requested setting,
   time, and visible result.
4. Return to the starting setting and check the result. Export diagnostics again.
5. Review the ZIPs and attach them with your observations to the
   [support testing issue](https://github.com/OpenBladeProject/openblade-core/issues/741).

Stay with the laptop during sleep/resume testing so you can resume it and check
the result. Reports stay local until you share them. You do not need to test every setting
or combination; report a specific problem if one appears.

## If controls pause

Export diagnostics, then choose **Resume laptop controls** in the notice.
OpenBlade checks the laptop and reads its current settings. After a successful
check, choose the setting again; the previous request is not replayed. For
lighting, select a lighting setting or change the active profile's lighting.
Failed checks keep controls paused.

A thermal-only pause does not require unrelated charge-limit readings on models
without temporary full charge. Pending battery operations still require their own
confirmed readback.

The [implementation reference](experimental-blade-reference.md) contains protocol
limits, diagnostic fields, and upstream sources.
