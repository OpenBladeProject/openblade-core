# Experimental Blade support

English | [简体中文](zh-CN/experimental-blade-support.md) | [Русский](ru-RU/experimental-blade-support.md)

OpenBlade recognizes these laptops automatically. Use a current pre-release to
try the controls and help test support. Hardware testing in OpenBlade is still
in progress.

## Available controls

| Laptop | Model number | Controls |
| --- | --- | --- |
| Blade 14 (2025) | `RZ09-0530` | Balanced, Silent, battery modes, Custom levels, keyboard and logo lighting, 50–100% charge limits |
| Blade 16 (2023) | `RZ09-0483` | Performance and Custom levels with CPU Boost, fixed and maximum fans, keyboard and logo lighting, 50–100% charge limits |
| Blade 14 (2023) | `RZ09-0482` | Balanced, Silent, Custom levels, fixed fans, keyboard and logo lighting, 50–100% charge limits |
| Blade 18 (2023) | `RZ09-0484` | Performance and Custom levels, maximum fans, keyboard and logo lighting, 50–100% charge limits |
| Blade 15 Base (Early 2020) | `RZ09-0328` | Gaming and Custom levels, single-zone keyboard lighting |

Close Synapse before using these controls, or use
[Synapse Blade Blocker](synapse-blade-blocker.md) after checking its model coverage
and applying the patch. OpenBlade checks the adapter's reported wattage before
external-power performance or fan changes. A confirmed limited adapter allows
Balanced and returning fans to Automatic; other external-power thermal changes
wait for confirmed full power. Models with fixed-speed control also offer
Automatic and fixed fans on battery. Fixed speed on a limited adapter is not
yet admitted for these experimental models. An unknown reading blocks the change. The
reading does not identify the connector. The Blade 14 (2025) also offers
Battery Saver and Battery Balanced on battery power.

- Fixed fan targets cover 2,000–5,000 RPM on the Blade 16 (2023), and
  2,200–5,000 RPM on the Blade 14 (2023). Automatic hands control back to firmware.
  Fan curves need a supported temperature source and are not yet available.
- Maximum Fans is a separate Custom-mode switch on the Blade 16 (2023) and
  Blade 18 (2023). Select Automatic fans before turning it on. The Blade 18 does
  not offer adjustable fixed fan targets. Maximum Fans cannot change while another
  Power Mode Control option is on, such as local dimming or temporary full charge.
  Before leaving Custom, OpenBlade confirms Maximum Fans is off, turns it off
  when needed, and checks again before changing the preset.
- Custom CPU/GPU levels are Low, Medium, and High. The Blade 16 (2023) also
  offers CPU Boost and High/High. Other models exclude High/High.
- The Blade 15 uses single-zone RGB lighting; Wave and logo controls are not
  supported. The newer models offer brightness, built-in effects, and logo
  lighting. Static RGB on the Blade 16 (2023) and Blade 18 (2023) is one color
  across the keyboard; per-key and spatial effects are not available.
  Firmware idle fade can restore the built-in effect; select Static again to repaint.
  The Blade 16 also offers whole-keyboard Ambient Awareness and Audio Meter.
  The Blade 15 offers random or single-color Breathing.
- HyperBoost can be reported on the Blade 16 (2023) and Blade 18 (2023), but
  OpenBlade does not control it. Turn it off in Synapse before changing performance.
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
the result. Reports stay local until you share them.

## If controls pause

Export diagnostics, then choose **Resume experimental controls** in the notice.
OpenBlade checks the laptop and reads its current settings. After a successful
check, choose the setting again; the previous request is not replayed. For
lighting, select a lighting setting or change the active profile's lighting.
Failed checks keep controls paused.

The [implementation reference](experimental-blade-reference.md) contains protocol
limits, diagnostic fields, and upstream sources.
