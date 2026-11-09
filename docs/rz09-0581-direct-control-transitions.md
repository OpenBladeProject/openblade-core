# Blade 16 direct control transitions

The service applies admitted destinations without a separate Balanced or
Automatic preparation step on RZ09-0581 (2026) and RZ09-0528 (2025). Each
production factory uses its own exact-device admission and complete captured
manual state/target command set. RZ09-0581 uses its trusted embedded fixture;
RZ09-0528 uses its code-defined captured catalog and 2000–5000 RPM range.
Other backends retain their existing thermal transition path. An omitted
capability defaults to false. Direct-transition capability was introduced in
service IPC version 64; the limited-power manual fan capability requires IPC
version 66 and a coordinated component update. The protocol-51 read-only
projection omits these fields; Session Agent IPC remains unchanged.

## Command paths

- Automatic to Fixed, Maximum, or Manual Curve uses paired manual entry and
  target commands. On full adapter power, Silent, Performance, and Custom keep
  their selector. An already-manual state needs only target updates. A known
  battery selector uses fresh source evidence to select the battery or
  external-power path.
- Changing manual fan subtype preserves manual state and submits the requested
  targets. It does not cycle through Automatic and Balanced.
- An explicit profile transition to Automatic applies the selected AC performance
  pair directly. A fan-only handback on full adapter power uses the confirmed
  Silent, Performance, or Custom selector with flag `00`. Custom reapplies
  and confirms its CPU/GPU levels. Generic `00/01` readback has no confirmed
  preset and uses the existing Balanced handback when no profile is requested.
- HyperBoost enters directly from admitted automatic or manual state on either
  supported cooling-pad path. Each command rechecks the selected path's pad
  presence and adapter eligibility; the operation cannot switch paths mid-entry.
  The durable journal retains the saved performance and fan intent before the
  first command. When manual firmware state hides the performance selector,
  the saved mode is restoration intent, not guessed firmware confirmation.
  A request to leave mode 7 for an admitted AC preset uses that preset's
  pair and final readback directly, including the Custom levels if requested.
- Battery Protection Off uses its admitted command from the current supported
  limit, independently of the unused stored limit. Re-enabling requires and
  applies an admitted stored limit; temporary full-charge cancellation
  remains a necessary dependency when it is active.

## Profiles, readback, and recovery

AC profile validation accepts independent admitted performance and manual-fan
intent on direct-capable devices. Runtime normalization preserves that intent.
The legacy generic manual pair (`00/01`) does not identify a preset. The
preset-specific pairs can confirm Silent, Performance, or Custom, and Custom
also reads its CPU and GPU levels. These reads cannot establish a fixed RPM
target, curve subtype, or effective power limits. Performance changes requested
while manual fans are already active remain saved for Automatic.

Every command retains fresh identity, capability, controller, power, session,
ownership, cancellation, and readback admission. An ambiguous submitted write
stops the transition and keeps its pending confirmation or recovery journal;
no default-state fallback or uncertain command replay is attempted.

The [fan power-source policy](fan-power-source-policy.md) extends the existing
typed fan paths to fixed speed on battery and fixed speed or curves on USB-C.
Owned manual fans still hand back to firmware Automatic at shutdown. This change
does not remove keyboard Normal-mode handoff or lifecycle ownership cleanup.

The reusable captured-command and attended validation evidence is maintained in
[PR #751](https://github.com/OpenBladeProject/openblade-core/pull/751). The Native
I/O serialization dependency is
[PR #753](https://github.com/OpenBladeProject/openblade-core/pull/753).

## Preserving performance during manual fan entry

[Issue #906](https://github.com/OpenBladeProject/openblade-core/issues/906)
reported a GPU power drop when enabling fixed fans from Performance or Custom
on RZ09-0581. The prior path sent selector `00` with manual flag `01`
regardless of the confirmed AC preset. Earlier direct-transition tests confirmed
manual fans and restoration under this sequence, without measuring retained
power limits.

The firmware also establishes why the old pair changes policy. On 0581,
[SO6120 MCU 1.07](so6120-mcu107-ec-fan-bridge.md#ordinary-fan-commands-reach-the-ec-clamp)
routes the selector in `0D/02` to EC bank-zero index `1A` and the manual flag
to index `36`. [EC 1.04](so6120-ec104-fan-analysis.md#manual-requests-and-the-selector-3-exception)
selects the normal fan policy using the mode byte separately from manual state.
On 0528, [SO6110 MCU 1.09](fan-stop-so6110-source-review.md) writes those fields
separately too. [EC 1.09](bios-ec-source-validation.md#custom-performance-charging-power-flags-and-version)
uses selector 4 to consume the separate Custom CPU and GPU settings.
A generic `00/01` command requests selector 0 as well as manual fans on these
paths. In SO6120 EC 1.04, the normal mode loader also copies table extension
byte `+0x39` to `NCTP` (`0x8020C`). The BIOS 4.01 NVIDIA `NVDA0820`
`_DSM` subfunction 2 exports `TGPA = 8 * NCTP`. Across the four factory
board groups, that raw byte is 10–30 for Balanced, 40–55 for Performance,
and 45–60 for Custom. These are firmware policy values. Their units, active
persistent-table values, and effective GPU wattage are unconfirmed.

The separately pinned [Synapse bundles](synapse-adapter-wattage.md#pinned-vendor-files)
for products 736 (RZ09-0581) and 710 (RZ09-0528) use the same contract. The
policy's active-performance handler combines the selected mode from main module
`3327` with the manual flag from module `50779`. The base-device bundles
send `0D/02 [1, channel, selector, 1]` and
`0D/01 [1, channel, target / 100]`. Both main bundles enable manual fans
in Custom, and the policy reapplies Custom CPU and GPU levels after setting the
fan mode and target. Limited external power uses the Balanced selector.

The production path keeps the selector (`05` Silent, `02` Performance, or
`04` Custom) on confirmed full adapter power. It verifies the paired selector
before sending targets, uses the existing typed setters to reapply the confirmed
Custom levels, and confirms the resulting state. It stops after an uncertain
write or disagreement. This applies to both exact-model production factories.
Paired HID GETs read the MCU cache. They confirm the controller's reported
state, while settled EC state and GPU wattage still need a physical round trip.

## RZ09-0528 evidence and validation scope

The 2025 factory retains its own paired `0D/02` manual-state commands and
`0D/01` target commands. Its captured mode-7 pair is pinned in
`Fixtures/Rz09_0528_02c6_2_02_HyperBoostProduction.json`; the
[exact vendor round trip](../evidence/legacy-captures/annotations/2026-09-27-rz09-0528-hyperboost-vendor-round-trip.json)
records that pair and Performance restoration with the cooling pad connected.
The shared implementation changes ordering, without introducing a command or
transferring another model's fan range or physical-validation results.

Tests exercise the actual RZ09-0528 production catalog for direct manual entry,
target-only updates, Performance/Custom exit and both admitted HyperBoost pad
paths. Shared service tests cover intent preservation and stopping after an
ambiguous write. The newly permitted RZ09-0528 sequences and connected-pad
manual-to-HyperBoost sequence on either model have not received a new physical
round trip. Prior RZ09-0581 results retain their recorded source identities.
