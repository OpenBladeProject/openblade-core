# Experimental Blade support

Pre-releases include community-derived controls for additional Blade laptops.
These models have not completed OpenBlade physical validation. Test reports help
us determine which controls can become fully supported.

## Automatic controls

OpenBlade enables the available experimental controls when it recognizes an exact
laptop profile below. There is no Settings switch or consent dialog. An older
saved experimental preference does not disable these controls.

The same model, interface, current-state, and competing-controller checks apply
to each change. A command that OpenBlade cannot confirm pauses experimental
writes and records the result in local diagnostics.

## Resume paused controls

Choose **Resume experimental controls** in the experimental-support notice.
OpenBlade drains the previous session, checks the laptop again, and reads the
current supported settings without writing them. When those checks succeed,
you can choose a setting to send a new request. The uncertain command is not
replayed automatically. Failed reads or checks leave the pause in place.

## Initial coverage

| Laptop | Exact Windows product name | USB identity | Initial controls |
| --- | --- | --- | --- |
| Blade 16 (2023), RZ09-0483 | `Blade 16 - RZ09-0483` | `1532:029F` | Balanced, Silent, 80% battery protection or off |
| Blade 14 (2023), RZ09-0482 | `Blade 14 - RZ09-0482` | `1532:029D` | Balanced, Silent, 80% battery protection or off |
| Blade 14 (2025), RZ09-0530 | `Blade 14 - RZ09-0530` | `1532:02C5` | Balanced, Silent, 80% battery protection or off |
| Blade 15 Base (Early 2020), RZ09-0328 | `Blade 15 Base Model (Early 2020) - RZ09-0328` | `1532:0255` | Balanced and Silent |
| Blade 18 (2023), RZ09-0484 | `Blade 18 - RZ09-0484` | `1532:02A0` | Balanced, Silent, Performance, bounded Custom levels, keyboard brightness and built-in effects, logo lighting, 50–100% charge limits in 1% steps (100% turns protection off) |

Performance changes require confirmed Automatic fans and fresh external-power
evidence. Manual fans, HyperBoost writes, and driver-dependent brightness integration
are not yet supported by this backend. Lighting and Custom controls below apply
only to the exact Blade 18 profile.
The 2020 Blade 15's battery commands are excluded because upstream reports that
its firmware echoes them without applying them. The other experimental battery profiles
accept only the observed 80% and off states.

For experimental models, the Plugged In profile covers externally powered
operation. Barrel-versus-USB-C classification and adapter wattage are unavailable.
This does not admit full-power-only features. On the 2025 Blade 14, thermal
changes also reject the firmware's battery-only modes rather than substituting
AC mode commands.

The Blade 18 profile comes from an exact-model owner report in the maintained
razer-ctl fork. It admits Balanced, Silent, and Performance. Custom CPU/GPU
Low, Medium, and High levels use an explicit experimental extrapolation of the
upstream protocol; they have not been confirmed on a Blade 18. Boost, Undervolt,
GPU clock offsets, and High/High are excluded. Custom changes require readable,
representable starting levels, including the stored levels before entering Custom,
and independent confirmation after each level change. Unsupported raw levels
remain diagnostic observations rather than a guessed setting. HyperBoost remains
observed-only: it cannot be enabled, though choosing Balanced, Silent, or Performance
can leave an existing firmware HyperBoost state. Custom requires an ordinary preset
as its starting state. Manual fan behavior is unresolved.

Blade 18 charge limits cover every whole percentage from 50% to 100%. Selecting
100% turns protection off; saved protected limits cover 50–99%. The exact-model
owner report confirms 80% and off. The expanded range is an explicit experimental
extrapolation from the maintained fork's battery-care encoding and Blade 16 tests.
A change first decodes the starting protection flag and threshold, sends one
setter, then independently reads the exact resulting limit. Setter acknowledgement
alone does not confirm the change. Unsupported enabled thresholds block changes
and retain the raw flag and threshold in diagnostics. Temporary full charge is
not available on this model.

Blade 18 keyboard controls include brightness, Off, Spectrum, random-color
Breathing, and Wave with upstream direction byte 1 (presented as Rightward).
Physical wave direction still needs tester confirmation. Logo controls include
Off, Static, and Breathing. Native Fn keys stay under firmware control; OpenBlade
does not change Driver mode or borrow another model's brightness-step sequence.
Static keyboard colors and host-rendered effects are not yet admitted: upstream
custom frames are documented as tested on the Blade 16, without an exact Blade 18
frame/layout validation.

Lighting changes require a readable starting effect and independent readback.
Unknown effects, unsupported parameters, and a non-Off effect whose brightness
rounds to 0% block changes and retain decoded diagnostics. Effect replies may
contain known zero padding; brightness and logo exchanges currently require the
exact three-byte reply shape. A different reply shape can block a query or pause
writes even when the firmware accepted a setter.

Returning to the same displayed brightness does not prove exact raw-register
restoration: the 0–255 register is presented as integer percentages. An Off logo
also hides its stored effect. Physical validation must preserve those values
through an admitted developer path before claiming exact restoration. This change
has not completed that validation.

Admission checks Razer and the exact product name in both Windows registry
sources, then the exact USB identity and 91-byte HID feature-report geometry.
Independent performance queries must identify a unique responding interface.
SKU and firmware versions are report metadata. Unknown models and ambiguous
interfaces remain blocked.

This update uses service IPC 62, configuration schema 19, and profile export
schema 11. Install the coordinated pre-release update; a tray-only replacement
cannot update the shared contracts. Older experimental preferences migrate to automatic support.

## Send a useful test report

1. Use a pre-release built from this change. In **About**, note the OpenBlade
   version and detected model. Close Synapse before testing.
2. Use **About → Export diagnostics** to save a baseline
   report. Note the current Synapse settings separately if needed.
3. Try one available setting and note the time,
   power source, starting setting, requested setting, and visible result.
4. Return to the starting setting and check the result. If OpenBlade cannot
   confirm a write, export diagnostics before choosing **Resume experimental
   controls**. After a successful current-state check, choose a new setting.
5. Export diagnostics again and attach the ZIPs to the
   [experimental support issue](https://github.com/OpenBladeProject/openblade-core/issues/741)
   with your observations, including any difference between the displayed and actual result.

OpenBlade checks replies and queries the resulting state independently. An
ambiguous write or readback pauses further experimental writes for that service
run. Resume checks the current state before allowing another request; collect
diagnostics first to preserve failure evidence. A successful reply alone is not physical validation. Sleep/resume tests
must be attended by a user who can resume the laptop.

## Diagnostic evidence

Exports read cached data without probing or writing the laptop again. Reports
remain local until the user chooses to share them. There is no background upload.

Alongside the existing version, firmware, capability, configuration, command
history, and device-verification files, experimental devices include
`experimental-support.json`. It records the pinned source, an `UpstreamDerived`
evidence level, write-pause state, and the last 32 typed observations. These describe
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
