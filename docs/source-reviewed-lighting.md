# Source-reviewed lighting coverage

The issue [#959](https://github.com/OSSBlade/openblade-core/issues/959)
diagnostics identify RZ09-0330 / `1532:0253`. Its thermal interface was admitted,
but the lighting factory rejected it before opening a lighting session. The
keyboard-driver messages concern SnapTap and function-row input handling.

The lighting review covers all 22 supported profiles. Existing capture-backed
and upstream-backed lighting backends retain their own admission and evidence.
The new controls use each exact model's pinned Synapse definitions and, where
available, its keyboard MCU image. Physical validation of the additions remains
pending. No updater, driver installation or hardware control was run during this
review.

| Models | Lighting controls | Evidence |
| --- | --- | --- |
| RZ09-0581, RZ09-0528 | Existing physically validated backends, unchanged | Existing capture admission |
| RZ09-0483, RZ09-0482, RZ09-0530, RZ09-0484, RZ09-0328 | Existing source-reviewed backends, unchanged | Existing exact-model vendor/upstream admission |
| RZ09-0287, RZ09-0301, RZ09-0314, RZ09-0330, RZ09-0370, RZ09-0409, RZ09-0508 | Independent keyboard brightness; logo Off, Static and Breathing | Exact Synapse model, subclass chain, LED getters/setters and control list |
| RZ09-0300, RZ09-0351 | Independent keyboard brightness | Exact Synapse control list has no logo controls |
| RZ09-0421, RZ09-0423, RZ09-0427 | Brightness; keyboard Off, random Breathing, Spectrum and Wave; logo controls on 0423/0427 | CH580, DI780 and PI480 MCU paths plus exact Synapse definitions |
| RZ09-0509, RZ09-0510, RZ09-0529 | Brightness; keyboard Off, random Breathing and Wave; logo Off, Static and Breathing | KI8100, SO6100 and KI8110 MCU paths plus exact Synapse definitions |

## Independent controls and remaining gaps

The nine independent-control profiles expose their available brightness and logo fields
separately from keyboard effects. Each edited field needs its own valid baseline
and independent readback. A brightness edit does not select an effect or rewrite
the logo. A logo edit does not rewrite brightness. Unknown fields stay unknown;
the existing scoped profile, recovery and suppression behavior is retained.

Their Synapse matrix setter is `03/0A`, not the newer `0F/02` API. A matching
keyboard-effect getter has not been established for those profiles. Their UI
effect lists are recorded as observed features, without admitting effect or RGB
frame writes. The RZ09-0330 entries in Razer's official firmware index list BIOS,
EC and Intel ME updates; the reviewed BIOS/EC packages contain no keyboard MCU
image. The review does not establish that no MCU image exists elsewhere.

The [BIOS/EC follow-up](../evidence/source-reviewed-blades/2026-11-13-rz09-0330-bios-ec-lighting.json)
also inspected the exact BIOS 1.06 and EC 1.01 images. The EC does contain a
Razer USB command stack, but its consumed descriptor identifies `1532:F003`
(Razer EC), not keyboard `1532:0253`. Its class-03 branch returns the staged
request with success status and a fresh checksum, without reading lighting state;
class 0F is unsupported. The HID descriptor also differs from the keyboard's
required feature geometry. This success-looking echo is not a lighting getter.
BIOS ACPI and other EC paths expose generic transfers, without an established
keyboard target or effect-state contract. No validated embedded keyboard image
was identified in this bounded inspection. The issue archive reports EC 1.00;
the reviewed released image is 1.01, and neither version string attests running
bytes. No new command or access path is admitted by this follow-up.

The [complete model review](../evidence/source-reviewed-blades/2026-11-13-lighting-coverage.json)
pins source URLs, hashes, selected inheritance chains, selectors and excerpts.
It preserves the original admission records as history.

## MCU readback and exact setter paths

The [six-MCU review](../evidence/source-reviewed-blades/2026-11-13-modern-mcu-lighting.json)
pins official carriers and embedded HEX members, decoded image hashes, USB
identities, dispatch targets, instruction windows and active renderer consumers.
`0F/82` reads twelve payload bytes: effect, Wave direction/speed, color count
and both RGB triples. Static and colored Breathing can be read as baselines;
their color setters are not exposed because same-effect firmware shortcuts can
ignore RGB changes. Wave admits wire direction 1 and speed 40; other speeds and
unknown effect parameters are rejected. Wire direction 2 is observed without
inventing a restorable direction-1 baseline.

Bank 0 is active configuration. Bank 1 is saved configuration and can disagree
with active effects or brightness. The new paths query active bank 0. This is
configuration readback, not a measurement of emitted light or an attestation of
the user's installed MCU image.

- CH580, DI780 and PI480 use their selected Synapse `03/0A` setter. It clears
  the custom overlay before equality shortcuts and activates the saved LED 5
  configuration. That operation also copies saved brightness. OpenBlade reads
  active brightness again and applies the requested value after the effect,
  instead of treating the earlier brightness observation as still current.
- SO6100, KI8100 and KI8110 use `0F/02`, bank 0, LED 5 only. Random Breathing
  and Wave clear custom ownership before their equality shortcuts. Off returns
  before that cleanup when the mode is already Off. Only that exact case uses a
  confirmed random-Breathing-to-Off transition, with requested zero brightness
  applied first. Every submission gets fresh admission; a failed bridge stops
  before the destination without replay. Legacy setters on KI8110 also modify
  LED 38, so that path is not used.
- Spectrum remains observed-only on those three newer images. Its policy and
  separate palette state cannot be established by a matching effect-mode byte.

The existing exact-host, HID descriptor, competing-controller, session,
serialization and per-submission checks remain authoritative. Failed readback,
cancellation after possible submission or an uncertain result pauses writes.
Recovery observes fresh state and does not replay the request.

Reproduce the offline MCU proof against a locally acquired pinned HEX or decoded
image, optionally including its official carrier:

```powershell
python tools/firmware/verify_source_reviewed_lighting.py <firmware> --carrier <carrier>
```

The verifier checks immutable source identity and the reviewed instruction and
dispatch bindings. Vendor firmware bytes are not committed. The focused software
tests cover the field-only paths, active-versus-saved brightness, direct builtin
transitions, the concrete Off cleanup dependency and stop-on-ambiguity behavior.
