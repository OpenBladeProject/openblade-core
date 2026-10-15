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

- Automatic to Fixed, Maximum, or Manual Curve uses the existing paired manual
  entry and target commands. An already-manual state needs only target updates.
  A known battery selector uses fresh source evidence to select the battery
  or external-power path; it needs no preset reset.
- Changing manual fan subtype preserves manual state and submits the requested
  targets. It does not cycle through Automatic and Balanced.
- Manual to Automatic applies the selected AC performance pair directly. Custom
  additionally applies and confirms the saved CPU/GPU levels. The service
  attributes the coupled fan and performance write together.
- HyperBoost enters directly from admitted automatic or manual state on either
  supported cooling-pad path. Each command rechecks the selected path's pad
  presence and adapter eligibility; the operation cannot switch paths mid-entry.
  The durable journal retains the saved performance and fan intent before the
  first command. When manual firmware state hides the performance selector,
  the saved mode is restoration intent, not guessed firmware confirmation.
- Battery Protection Off uses its admitted command from the current supported
  limit, independently of the unused stored limit. Re-enabling requires and
  applies an admitted stored limit; temporary full-charge cancellation
  remains a necessary dependency when it is active.

## Profiles, readback, and recovery

AC profile validation accepts independent admitted performance and manual-fan
intent on direct-capable devices. Runtime normalization preserves that intent.
Manual readback confirms only generic manual state; it cannot establish a fixed
RPM target, curve subtype, or effective performance limits. Full reconciliation
therefore does not mark latent performance as firmware-confirmed.

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

## RZ09-0528 evidence and validation scope

The 2025 factory retains its own paired `0D/02` manual-state commands and
`0D/01` target commands. Its captured mode-7 pair is pinned in
`Fixtures/Rz09_0528_02c6_2_02_HyperBoostProduction.json`; the
[exact vendor round trip](https://github.com/OpenBladeProject/openblade-captures/blob/4f2bf1565c7557439760db7e45104bb1e8b62bfd/annotations/2026-09-27-rz09-0528-hyperboost-vendor-round-trip.json)
records that pair and Performance restoration with the cooling pad connected.
The shared implementation changes ordering, without introducing a command or
transferring another model's fan range or physical-validation results.

Tests exercise the actual RZ09-0528 production catalog for direct manual entry,
target-only updates, Performance/Custom exit and both admitted HyperBoost pad
paths. Shared service tests cover intent preservation and stopping after an
ambiguous write. The newly permitted RZ09-0528 sequences and connected-pad
manual-to-HyperBoost sequence on either model have not received a new physical
round trip. Prior RZ09-0581 results retain their recorded source identities.
