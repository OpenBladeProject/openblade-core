# Source-reviewed lighting coverage

The issue [#959](https://github.com/OSSBlade/openblade-core/issues/959)
diagnostics identify RZ09-0330 / `1532:0253`. Its thermal interface was admitted,
but the lighting factory rejected it before opening a lighting session. The
keyboard-driver messages concern SnapTap and function-row input handling.

This MCU-first pass covers all 22 supported profiles, including brightness,
logo, effects, frames, input mapping, ownership and restoration. Where an exact
keyboard MCU was unavailable, the review checked pinned OpenRazer and OpenRGB
sources. Source review and physical validation remain separate. No hardware
control, driver installation or firmware updater execution was performed.

| Models | Lighting controls | Main evidence |
| --- | --- | --- |
| RZ09-0581, RZ09-0528 | Existing validated backends retain their own controls and admission | Own capture evidence and regional MCU maps |
| RZ09-0483, RZ09-0482, RZ09-0530, RZ09-0484, RZ09-0328 | Existing source-reviewed backends retain their controls | Exact-model vendor/upstream definitions and available MCU proofs |
| RZ09-0287, RZ09-0314, RZ09-0330, RZ09-0370, RZ09-0409, RZ09-0508 | Independent keyboard brightness; logo Off, Static and Breathing | Exact Synapse LED getters/setters; upstream cross-check |
| RZ09-0300, RZ09-0351 | Independent keyboard brightness | Exact vendor control list has no logo controls |
| RZ09-0301 | Brightness; keyboard Off, Static, Breathing, Spectrum and Wave; logo controls; independent field editing retained | Exact PID 0245 image embedded in the official keyboard updater |
| RZ09-0421 | Brightness; keyboard Off, Breathing, Spectrum and Wave | CH580 MCU and exact vendor definitions |
| RZ09-0423, RZ09-0427 | Brightness; keyboard Off, Static, Breathing, Spectrum and Wave; logo controls | DI780 and PI480 MCUs and exact vendor definitions |
| RZ09-0510 | Brightness; keyboard Off, Breathing and Wave; logo controls | SO6100 MCU and exact vendor definitions |
| RZ09-0509, RZ09-0529 | Brightness; keyboard Off, Static, Breathing and Wave; logo controls | KI8100 and KI8110 MCUs and exact vendor definitions |

All fifteen expanded profiles support independent brightness and available logo
editing, even when the keyboard effect is unknown. All seven MCU-backed additions support random, single-color and dual-color
Breathing, and both Wave directions. Static is unavailable on RZ09-0421 and
RZ09-0510 because their renderers can select a hidden palette instead of the
stored RGB returned by the getter. Spectrum remains observed-only on the three
newer images because its separate policy/palette is not reported.

The [complete per-model ledger](../evidence/source-reviewed-blades/2026-11-13-complete-lighting-pass.json)
records the admitted controls and remaining gaps. The
[MCU inventory](../evidence/source-reviewed-blades/2026-11-13-mcu-availability.json)
checks embedded updater resources as well as standalone files. Earlier review
records remain as history; the parameter/consumer follow-ups below refine their
initial admission decisions without changing their pinned source bytes.

RZ09-0581 also adds Fire as a deterministic software pattern through its own
6×17 canvas, regional MCU projection and existing frame transport. Its semantic
admission records `SoftwarePatternWithoutPhysicalValidation` and physical status
`Pending`. The original capture oracle and known-answer wire frame are retained;
this does not inherit RZ09-0528's Fire oracle.

## Independent controls and RZ09-0330 BIOS/EC

Fifteen profiles expose independent brightness and available logo fields. Each
edited field has its own baseline and independent confirmation. A brightness
edit does not select an effect or rewrite the logo. A logo edit does not rewrite
brightness. Unknown fields remain unknown.

RZ09-0301 additionally has the MCU effect path below. Its field-only profiles
remain field-only, and old unmanaged profiles remain unmanaged. Selecting an
effect explicitly records keyboard-effect intent; firmware readback never
invents that intent. Profile/config schema 26, override schema 9 and profile
export schema 17 migrate old values with that marker false. The coordinated
product update uses service IPC 81; display-only Session IPC 2 remains unchanged.

For the eight profiles without an established MCU effect getter, the exact Synapse matrix
setter is `03/0A`. A matching effect getter is not established. Their vendor UI
effects are recorded as observed features, without effect or RGB frame writes.

The [RZ09-0330 BIOS/EC review](../evidence/source-reviewed-blades/2026-11-13-rz09-0330-bios-ec-lighting.json)
inspects BIOS 1.06 and EC 1.01. The EC has a real Razer USB stack, but its active
descriptor identifies `1532:F003` (Razer EC), rather than keyboard `1532:0253`.
Its class-03 branch returns the staged request with success and a fresh checksum
without reading lighting state; class 0F is unsupported. Its HID descriptor also
differs from the required keyboard feature geometry. That echo is not a getter.

BIOS ACPI and other EC paths expose generic transfers, without an established
keyboard target or effect-state contract. No validated embedded keyboard image
was identified in this bounded inspection. The official index lists BIOS, EC
and Intel ME updates; this does not prove no other MCU source exists. The issue
archive reports EC 1.00, while the reviewed released image is 1.01. Neither
version string attests running bytes. This follow-up admits no new EC route.

## OpenRazer and OpenRGB fallback

The [pinned upstream review](../evidence/source-reviewed-blades/2026-11-13-upstream-lighting.json)
records immutable commits, source hashes and exact model paths. Both
[OpenRazer](https://github.com/openrazer/openrazer/blob/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d/daemon/openrazer_daemon/hardware/keyboards.py#L1894-L1908)
and [OpenRGB](https://github.com/CalcProgrammer1/OpenRGB/blob/4e7aa49d68aca6c959eb2a9b48a6d6fc35f744a6/Controllers/RazerController/RazerDevices.cpp#L3579-L3598)
identify keyboard `1532:0253` and declare legacy effects and 6×16 frame addressing.

Neither reviewed implementation supplies an independent current-effect or RGB
baseline. OpenRazer's matrix effect attributes are write-only; `getEffect`
returns daemon cache, and restoration replays remembered settings. OpenRGB's
`GetActiveMode` and `GetColor` return application memory; its Razer controller
does not save and restore the prior firmware effect. A submission acknowledgement
does not fill that gap. These findings leave the eight remaining models'
brightness/logo scope unchanged. RZ09-0301's new effects use its own MCU proof.

OpenRazer has exact PID definitions for all nine models in the MCU-availability audit;
OpenRGB has eight, with `02B6` absent. Base-model geometry/method declarations
differ, so generic upstream records do not override exact vendor definitions or
establish a physical input-to-light map. OpenRazer's alternate Blade brightness
API is recorded without replacing the reviewed Synapse brightness path.

## MCU parameters, transitions and recovery

The [six-MCU review](../evidence/source-reviewed-blades/2026-11-13-modern-mcu-lighting.json)
pins official carriers, embedded HEX members, decoded image hashes, USB identity,
dispatch and active renderer consumers. The
[color/Wave follow-up](../evidence/source-reviewed-blades/2026-11-13-mcu-color-wave-review.json)
adds setter shortcuts, color consumers and each selected vendor model's direction
labels. Leftward is wire 1; Rightward is wire 2, at speed 40. Unknown parameters
and other speeds are rejected.

RZ09-0301's [MCU proof](../evidence/source-reviewed-blades/2026-11-13-rz09-0301-lighting-mcu.json)
and [independent counterreview](../evidence/source-reviewed-blades/2026-11-13-rz09-0301-lighting-counterreview.json)
cover the image reconstructed from `DeviceUpdater.resources` in the official
RZ09-03017/03018 updater. Its active descriptor/configuration, dispatch and color
consumers were reviewed separately. A configurable PID template does not replace
fresh exact `1532:0245` runtime admission.

Bank 0 is active; bank 1 is saved and can disagree with active effects or
brightness. `0F/82` reads twelve payload bytes, including effect, direction/speed,
color count and RGB triples. This is configuration readback, not a measurement of
emitted light or an attestation of the installed MCU image.

- RZ09-0301, CH580, DI780 and PI480 use their selected `03/0A` setter. It clears
  custom ownership and activates saved LED 5 configuration, including brightness.
  OpenBlade confirms the effect, reads active brightness again and applies the
  requested brightness if needed. Static and colored Breathing copy colors
  directly; no intermediate effect is required.
- SO6100, KI8100 and KI8110 use `0F/02`, bank 0, LED 5. Their already-Off shortcut
  precedes custom ownership cleanup. Only that exact case uses a confirmed random
  Breathing-to-Off transition, with requested zero brightness applied first.
- Those three colored Breathing setters skip RGB changes when the color count is
  unchanged. Only a changed color at the same count uses a confirmed count-0
  random-Breathing bridge. KI8100 needs that bridge for a changed Static color;
  KI8110 updates Static RGB directly. Color-count changes remain direct.
- Mode 8 identifies custom output without pixels. A typed built-in destination
  can replace that known state directly. It never becomes a guessed framebuffer
  baseline, and mode 8 cannot confirm a submitted built-in destination.

Each submission retains exact-host, HID, controller, session, serialization and
fresh native-boundary admission. Failed or uncertain writes stop further writes;
recovery observes actual state and never replays a request. Field-only faults
recover from independently read fields without requiring an unrelated effect
baseline. Mixed obligations widen to full recovery; a field observation never
discharges an effect fault. A valid custom state may remain unknown in a fresh
recovery observation; its pixels are not fabricated.

## Frames and input mapping

The new MCU records establish frame bounds, but no new custom-frame backend is
admitted here. Activation, ownership lifecycle and durable restoration need a
complete reviewed path. Neither mode 8 nor application memory reconstructs pixels.

For the existing RZ09-0482/RZ09-0483 grid backends, the additional
[PI490](../evidence/source-reviewed-blades/2026-11-13-rz09-0482-descriptor-followup.json)
and [SO690](../evidence/source-reviewed-blades/2026-11-13-rz09-0483-descriptor-followup.json)
inspection pins selected descriptor tables, lookup paths and RGB consumers.
Auxiliary routing, full command-copy linkage, Windows input bindings and reviewed
fixtures remain incomplete, so those findings do not enable key-triggered effects
or per-key imports. RZ09-0530 retains its intentionally bounded common regional
bindings. These backends do not inherit another model's map.

Reproduce the initial six-MCU proof against a locally acquired pinned HEX or
decoded image, optionally including its official carrier:

```powershell
python tools/firmware/verify_source_reviewed_lighting.py <firmware> --carrier <carrier>
```

Vendor firmware bytes are not committed. Focused software tests cover direct
color/count changes, necessary bridges, dark state, custom-to-built-in changes,
independent field recovery and stop-on-ambiguity behavior. Physical validation
of the additions remains pending.
