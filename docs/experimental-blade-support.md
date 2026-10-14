# Experimental Blade support

Pre-releases include community-derived controls for additional Blade laptops.
These models have not completed OpenBlade physical validation. Test reports help
us determine which controls can become fully supported.

## Enable controls

Open **Settings → Experimental laptop controls**, turn the switch on, and choose
**Enable controls** after reading the explanation. The switch starts off. An
existing **Lab features** preference does not grant this permission.

The service saves consent in the current Windows user's configuration and
enforces it for direct commands and automation, including queued writes. Turning
the switch off blocks subsequent experimental writes. It does not undo settings
already applied to the laptop. Revocation closes write admission immediately, even
if another operation is running or saving the preference fails. A failed save
keeps controls disabled for the current service session and reports that the
preference has not been saved; a later explicit enable can restore consent.

## Initial coverage

| Laptop | Exact Windows product name | USB identity | Initial controls |
| --- | --- | --- | --- |
| Blade 16 (2023), RZ09-0483 | `Blade 16 - RZ09-0483` | `1532:029F` | Balanced, Silent, 80% battery protection or off |
| Blade 14 (2023), RZ09-0482 | `Blade 14 - RZ09-0482` | `1532:029D` | Balanced, Silent, 80% battery protection or off |
| Blade 14 (2025), RZ09-0530 | `Blade 14 - RZ09-0530` | `1532:02C5` | Balanced, Silent, 80% battery protection or off |
| Blade 15 Base (Early 2020), RZ09-0328 | `Blade 15 Base Model (Early 2020) - RZ09-0328` | `1532:0255` | Balanced and Silent |
| Blade 18 (2023), RZ09-0484 | `Blade 18 - RZ09-0484` | `1532:02A0` | Balanced, Silent, 80% battery protection or off |

Performance changes require confirmed Automatic fans and fresh external-power
evidence. Custom CPU/GPU levels, manual fans, keyboard lighting, HyperBoost, and
driver-dependent brightness integration are not yet supported by this backend.
The 2020 Blade 15's battery commands are excluded because upstream reports that
its firmware echoes them without applying them. This first battery implementation
accepts only the observed 80% and off states.

For experimental models, the Plugged In profile covers externally powered
operation. Barrel-versus-USB-C classification and adapter wattage are unavailable.
This does not admit full-power-only features. On the 2025 Blade 14, thermal
changes also reject the firmware's battery-only modes rather than substituting
AC mode commands.

The Blade 18 profile comes from an exact-model owner report in the maintained
razer-ctl fork. Its Performance and HyperBoost states can be observed, but only
Balanced and Silent can be requested. Manual fan behavior remains unresolved;
OpenBlade does not expose fan writes for it. The upstream report also confirms
keyboard brightness, Spectrum/Wave/Breathing/Off effects, and logo lighting.
The effects are recorded as observed capabilities; lighting and brightness writes
remain unavailable in this backend.

Admission checks Razer and the exact product name in both Windows registry
sources, then the exact USB identity and 91-byte HID feature-report geometry.
Independent performance queries must identify a unique responding interface.
SKU and firmware versions are report metadata. Unknown models and ambiguous
interfaces remain blocked.

## Send a useful test report

1. Use a pre-release built from this change. In **About**, note the OpenBlade
   version and detected model. Close Synapse before testing.
2. Before enabling controls, use **About → Export diagnostics** to save a baseline
   report. Note the current Synapse settings separately if needed.
3. Enable experimental controls. Try one available setting and note the time,
   power source, starting setting, requested setting, and visible result.
4. Return to the starting setting and check the result. If OpenBlade cannot
   confirm a write, stop further changes and export diagnostics before restarting.
   Do not repeat an uncertain write.
5. Export diagnostics again and attach the ZIPs to the
   [experimental support issue](https://github.com/OpenBladeProject/openblade-core/issues/741)
   with your observations, including any difference between the displayed and actual result.

OpenBlade checks replies and queries the resulting state independently. An
ambiguous write or readback pauses further experimental writes for that service
run. Restarting begins a new run; collect diagnostics first to preserve failure
evidence. A successful reply alone is not physical validation. Sleep/resume tests
must be attended by a user who can resume the laptop.

## Diagnostic evidence

Exports read cached data without probing or writing the laptop again. Reports
remain local until the user chooses to share them. There is no background upload.

Alongside the existing version, firmware, capability, configuration, command
history, and device-verification files, experimental devices include
`experimental-support.json`. It records the pinned source, an `UpstreamDerived`
evidence level, consent, and the last 32 typed observations. These describe
queries, baselines, submissions, independent readbacks, and failures, with
requested and confirmed values when available.

The report includes collection time, power source, last confirmed settings, and
cached battery power, fan speed, and temperature readings when live readings are
enabled. Missing readings remain unavailable. History is bounded and held in
memory; it is not a complete audit or a continuous telemetry recording. Repeated
unchanged query failures are deduplicated, and rejected interface probes do not
replace control results. Enum values use readable names. Serial
numbers and raw HID reports are excluded. Sensitive identity values pass through
the existing diagnostic redaction.

## Source provenance

- [razer-ctl at 8e12e778](https://github.com/tdakhran/razer-ctl/tree/8e12e778d303d4ed01eeb2c1e99ce2e97f1c8917): model/PID mappings, the Blade 16 Synapse capture table, and the Blade 14 contributor report.
- [blade-control at bc48c916](https://github.com/jdsgnrinfo/blade-control/tree/bc48c916d4d6cc47df8c0e32a73ef454dda37cdd): tested-model reports and the 2020 Blade 15's command exceptions.

- [Maintained razer-ctl fork at 9c66e705](https://github.com/sqmagellan/razer-ctl/tree/9c66e7055a0e31f5ec25c16185e66fde1f50cf4a): Blade 18 profile and command definitions, based on [owner report #1](https://github.com/sqmagellan/razer-ctl/issues/1). The report confirms behavior on one RZ09-0484 unit; it does not supply raw captures or OpenBlade validation.

These references share protocol ancestry. Agreement is not two independent
physical validations. Experimental authority is separate from OpenBlade's
physically validated command catalog. It does not assert that OpenBlade
application, restoration, or lifecycle checks have already passed.

Testers still need to confirm application, independent readback, return to the
starting state, and relevant power/lifecycle behavior for each model and control.
Variants with physical differences need corresponding capability evidence before
new controls are exposed. Broader upstream PID tables are discovery leads.
