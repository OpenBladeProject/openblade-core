# RZ09-0581 direct control transitions

The service applies admitted destinations without a separate Balanced or
Automatic preparation step on RZ09-0581. The production factory advertises
`SupportsDirectManualFanTransitions` only for the trusted embedded fixture,
exact live identity, and complete captured manual state/target command set.
RZ09-0528 now also advertises direct manual fan transitions from its separately
reviewed Synapse mode/flag behavior; see the
[fan power-source evidence](fan-power-source-policy.md). Other backends retain
their existing thermal transition path. An omitted
capability defaults to false. The strict service codec now requires IPC version
66 for the limited-power manual fan capability and a coordinated component update. The protocol-51 read-only
projection omits the field; Session Agent IPC versions remain unchanged.

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
- Padless HyperBoost enters directly from admitted automatic or manual state.
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
