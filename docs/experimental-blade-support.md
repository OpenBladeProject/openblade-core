# Experimental Blade support

OpenBlade recognizes these laptops automatically. Use a current pre-release to
try the controls and help test support. Hardware testing in OpenBlade is still
in progress.

## Available controls

| Laptop | Model number | Controls |
| --- | --- | --- |
| Blade 14 (2025) | `RZ09-0530` | Balanced, Silent, 80% battery protection or off |
| Blade 16 (2023) | `RZ09-0483` | Balanced, Silent, 80% battery protection or off |
| Blade 14 (2023) | `RZ09-0482` | Balanced, Silent, 80% battery protection or off |
| Blade 18 (2023) | `RZ09-0484` | Balanced, Silent, Performance, Custom levels, keyboard and logo lighting, 50–100% charge limits |
| Blade 15 Base (Early 2020) | `RZ09-0328` | Balanced and Silent |

Close Synapse before using these controls. Performance changes need external
power and fans already in Automatic. Manual fans, HyperBoost, and native Windows
brightness integration are not yet supported. Plugged In settings cover both
barrel and USB-C power.

On the Blade 18:

- Custom offers Low, Medium, and High CPU/GPU levels, except High/High. Choose
  Balanced, Silent, or Performance before entering Custom from HyperBoost.
- Keyboard effects are Off, Spectrum, random-color Breathing, and Wave. The logo
  supports Off, Static, and Breathing. Static keyboard colors and interactive
  effects are not yet supported.
- Charge limits use 1% steps. Selecting 100% turns protection off. Temporary full
  charge is not yet supported.

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
