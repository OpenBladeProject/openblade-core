# Experimental Blade support

English | [简体中文](zh-CN/experimental-blade-support.md) | [Русский](ru-RU/experimental-blade-support.md)

OpenBlade recognizes these laptops automatically. Use a current pre-release to
try the controls and help test support. Hardware testing in OpenBlade is still
in progress.

## Available controls

| Laptop | Model number | Controls |
| --- | --- | --- |
| Blade 14 (2025) | `RZ09-0530` | Balanced, Silent, battery modes, Custom levels, keyboard and logo lighting, 50–100% charge limits, optional [THX audio](thx-audio.md) with a supported installed stack |
| Blade 16 (2023) | `RZ09-0483` | Performance and Custom levels with CPU Boost, HyperBoost with or without the cooling pad, fixed and maximum fans, keyboard and logo lighting, 50–100% charge limits |
| Blade 14 (2023) | `RZ09-0482` | Balanced, Silent, Custom levels, HyperBoost with or without the cooling pad, fixed fans, keyboard and logo lighting, 50–100% charge limits |
| Blade 18 (2023) | `RZ09-0484` | Performance and Custom levels, including every CPU Boost/GPU level combination and High/High, fixed fans in Balanced and maximum fans, keyboard and logo lighting, 50–100% charge limits |
| Blade 15 Base (Early 2020) | `RZ09-0328` | Gaming and Custom levels, single-zone keyboard lighting |

Close Synapse before using these controls, or use
[Synapse Blade Blocker](synapse-blade-blocker.md) after checking its model coverage
and applying the patch. OpenBlade checks the adapter's reported wattage before
external-power performance or fan changes. A confirmed limited adapter allows
Balanced and returning fans to Automatic; other external-power thermal changes
wait for confirmed full power. Models with fixed-speed control also offer
Automatic and fixed fans on battery. Blade 18 fixed fans also remain in Balanced
on battery. Fixed speed on a limited adapter is not
yet supported for these experimental models. If wattage cannot be confirmed,
the change is blocked. The Blade 14 (2025) also offers
Battery Saver and Battery Balanced on battery power.

- Fixed fan targets cover 2,000–5,000 RPM on the Blade 16 (2023), and
  2,200–5,000 RPM on the Blade 14 (2023). The Blade 18 (2023) offers one
  2,200–3,800 RPM control for both fans in Balanced. Fan curves are not yet supported.
- Maximum Fans is a separate Custom-mode switch on the Blade 16 (2023) and
  Blade 18 (2023). Select Automatic fans before turning it on. Maximum Fans cannot
  change while another
  Power Mode Control option is on, such as local dimming or temporary full charge.
  OpenBlade turns Maximum Fans off before leaving Custom.
- Custom CPU/GPU levels are Low, Medium, and High. The Blade 16 (2023) also
  offers CPU Boost and High/High. Blade 18 (2023) offers all twelve CPU/GPU
  combinations, including Boost/Medium and High/High, using Synapse's independent
  level controls.
- The Blade 15 uses single-zone RGB lighting; Wave and logo controls are not
  supported. The newer models offer brightness, built-in effects, and logo
  lighting. Static RGB on the Blade 16 (2023) and Blade 18 (2023) is one color
  across the keyboard; individual key colors are not yet supported.
  Firmware idle fade can restore the built-in effect; select Static again to repaint.
  Blade 16 (2023) and Blade 18 (2023) also offer whole-keyboard Ambient Awareness
  and Audio Meter.
  The Blade 15 offers random or single-color Breathing. Additional Synapse
  effects observed for the Blade 18 are recorded in diagnostics. Blade 18 also
  offers experimental Fire, clockwise or counterclockwise Wheel, and Starlight.
  Fire repeats a short recorded sample; Wheel plays a recorded cycle. Starlight
  uses the shared twinkle pattern over this model's RGB cells. Reactive, Ripple
  and Tidal use [recovered Synapse definitions](blade18-spatial-lighting.md).
  The physical keyboard region selects the matching Synapse map, including
  regional Enter, backslash, Alt, ISO and Japanese keys. If the region cannot be
  read, keys with conflicting regional bindings do not trigger these effects.
- Blade 18 (2023) offers the [default Synapse Fn shortcuts](blade18-fn-decoder.md#default-shortcuts):
  performance, refresh rate, touchpad, F1–F12, arrows and Delete. The decoder and
  actions are source-backed; behavior on the laptop still needs testing.
- Blade 14 (2023) and Blade 16 (2023) offer experimental HyperBoost on full-power
  AC, with or without the Razer Laptop Cooling Pad. They require MCU firmware
  1.1.1.3 or later on the Blade 14, and 1.5.0.0 or later on the Blade 16.
  On the Blade 14, turn off
  Maximum Fans in Synapse first if it was enabled there; OpenBlade cannot yet
  restore that option on this model.
  Blade 18 (2023) remains observed-only: turn HyperBoost off in Synapse before
  changing performance.
- Charge limits use 1% steps; 100% turns protection off. The Blade 15 does not
  support charge limits. Temporary full charge is not yet supported.

## Send a useful test report

1. Install the complete [pre-release](https://github.com/OpenBladeProject/openblade-core/releases).
   Note the version and detected model in **About**.
2. Use **About → Export diagnostics** to save a baseline ZIP.
3. Try one setting. Record the power source, starting setting, requested setting,
   time, and visible result.
4. Return to the starting setting and check the result. Export diagnostics again.
5. Review the ZIPs and attach them with your observations to the
   [experimental support issue](https://github.com/OpenBladeProject/openblade-core/issues/741).

Stay with the laptop during sleep/resume testing so you can resume it and check
the result. Reports stay local until you share them. You do not need to test every setting
or combination; report a specific problem if one appears.

## If controls pause

Export diagnostics, then choose **Resume experimental controls** in the notice.
OpenBlade checks the laptop and reads its current settings. After a successful
check, choose the setting again; the previous request is not replayed. For
lighting, select a lighting setting or change the active profile's lighting.
Failed checks keep controls paused.

The [implementation reference](experimental-blade-reference.md) contains protocol
limits, diagnostic fields, and upstream sources.
