# Native Service keyboard lighting (slice L1)

A Service built with `service-lighting-v1` serves `setLightingOverride` on
exact RZ09-0581 (`1532:02E0`). Slice L1 admits the keyboard Off with every
logo mode; slice L2 adds the matrix and the lit effects on the same path. The
feature is source-reviewed against managed and the reviewed capture evidence,
has software failure and recovery tests, and has not been run on a laptop.
Both package readers refuse it by name, so no packaged image carries it.

## Commands and admission

The job sends only commands the reviewed RZ09-0581 lighting evidence admits:

| Command | Use | Source |
|---|---|---|
| 03/83 | keyboard brightness query | `Rz09_0581_02e0_3_01_LightingSemanticAdmission.json` `brightness.query` |
| 03/03 | keyboard brightness set | `brightness.setPrefix` |
| 03/80 | logo state query | `logo.queryState` |
| 03/82 | logo effect query | `logo.queryEffect` |
| 03/00 | logo state set | `logo.setStatePrefix` |
| 03/02 | logo effect set | `logo.setEffectPrefix` |

The setters exist only behind the protocol crate's `lighting-setters`
feature, which only `service-lighting-v1` enables, and one fixed-length
builder (`openblade-protocol/src/lighting.rs`) encodes all six; the
encoder allowlist (`device_mode_guard_tests.rs`) records it.

Admission mirrors managed `ProductionLightingEvidenceSource.IsProductionLightingImplementationApproved`
(`ProductionLightingFactory.cs:74`): the embedded semantic admission manifest
must hash to managed's approved SHA-256 (`9F70CE88...DEED31`), and its device,
control envelope, brightness and logo sections, and Off among the supported
effects, must equal what the native codec builds
(`performance/lighting_admission.rs`). Any difference refuses every lighting
write. The matrix section and the lit effects are compared by slice L2 under
the same pin, together with the oracle (`ReadValidatedOracle`).

Each exchange runs under fresh exact-device, competing-controller, power,
session and ownership admission (`Fresh::authorize`), through the Service's
own HID lease. The Service stays the only HID owner.

## Request order

The order is managed `ExecuteScopedMutationAsync` (ServiceControlRuntime.cs
14868-15060) for the Lighting subsystem:

1. Low-power gate set (lifecycle triggers): the intent is stored and the
   reply is Stored, "Lighting intent was stored and will be applied after
   standby." (`DeferLightingIntentForLowPower`, 20209). Nothing is read or
   written.
2. A possible earlier submission left unconfirmed: a read-only job runs, the
   reply is managed's pending refusal (`PendingWriteBlockedResult`, 19255),
   and a complete read clears the doubt. Nothing is replayed.
3. Power: lighting reads no adapter, so it cannot tell a classified adapter
   from an unclassified one. Every AC request meets managed's unclassified
   admission (ServiceControlRuntime.UnclassifiedExternalPower.cs 60-84):
   fresh Windows AC presence that agrees with status, and HyperBoost neither
   confirmed active nor pending restore. Otherwise the reply is Blocked with
   managed's unclassified-adapter text (14935-14943), before anything is
   stored.
4. The intent is stored (`SetLightingOverrideCoreAsync`, 14820) in
   `manual-overrides.json` `overrides.lighting`, in managed's format and
   member order, normalized as managed `SaveAsync` normalizes it. Only a
   persistent override is saved; a temporary one clears the saved one and is
   held in memory for its power class. A failed store is managed's rejected
   intent (`RecordRejectedIntent`, 20495).
5. Apply: read the baseline, write the brightness, read it back, then the logo
   effect (for a lit mode) and state, each read back, then managed's
   aggregate read. A confirmed mismatch restores the baseline. A setter that
   may have been submitted without confirmation stops the request and
   latches the doubt in step 2. A reply other than Applied is prefixed
   "{committed} Hardware result: ", as managed prefixes it.

The 750 ms Off confirmation runs only under lighting ownership, which slice
L1 never holds.

## Status

The status projects `lightingOverride` and `desiredState.lighting` (source 4
for a persistent override, 3 for a temporary one) from the store. A request's
fresh reading joins the confirmed state a thermal read established:
`confirmedState.lighting` is the Off record for a zero brightness and null
otherwise (managed `ReadStateCoreAsync`, ProductionLightingPort.cs 186), and
`confirmedState.logoLighting` is the managed wire value.

## Interim divergences

Each is named by a test and removed by slice L2:

- Capabilities admit the keyboard Off only, with every logo mode. A lit
  effect is stored and then refused as managed refuses an effect its
  capability table lacks
  (`lit_effects_are_refused_until_l2_interim_divergence`).
- Clearing the override stores the clear and applies no profile lighting.
  Managed applies the profile's lighting there (ControlCoordinator.cs
  `ApplyLightingOutcomeAsync`).
- Power-source transitions (`ApplyFullTransitionAsync`, ServiceControlRuntime.cs
  14948-14956) and profile reconciliation run no lighting step, and a
  restart writes no lighting.
- The periodic thermal observation does not read lighting, so the next
  thermal read publishes the lighting members as null until a lighting
  request reads them again. Managed reads lighting in every observation.
- `setExternalLightingOverridePolicy` is not served and gets the generic
  unsupported reply.

## Stricter than managed

- On AC, every lighting request needs managed's unclassified-adapter
  admission, because lighting reads no adapter. Managed admits a classified
  adapter without the HyperBoost condition. Managed also refuses there while
  its status blocks writes; native status blocks writes for reasons of its
  own, so fresh admission before each exchange stands in for that member.
- The saved-override loader refuses a document managed would migrate (an
  older schema, or lighting that managed's visibility normalization changes)
  and preserves its bytes for managed.

## Validation still needed

Attended validation on exact RZ09-0581: Off from a lit keyboard with each
logo mode, readback confirmation, a deliberate restore after a mismatch, and
restoration of the saved baseline afterwards.
