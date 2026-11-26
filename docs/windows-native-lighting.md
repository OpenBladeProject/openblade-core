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
encoder allowlist (`device_mode_guard_tests.rs`) records it, and the lighting
guard (`lighting_guard_tests.rs`) pins its users and IDs.

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

1. Low-power gate set (lifecycle triggers): the intent is stored and nothing
   is read or written. A temporary override needs Windows battery, the one
   class known without an adapter read. The reply is Stored with the
   committed message (see the interim divergences).
2. A possible earlier submission left unconfirmed: a read-only job runs, the
   reply is managed's pending refusal (`PendingWriteBlockedResult`, 19255),
   and a complete read clears the doubt. Nothing is replayed.
3. Keyboard delegated to Windows Dynamic Lighting in the stored preferences
   (`IsKeyboardLightingDelegated`, 15213): Blocked, managed's delegation text,
   before anything is stored. Without a profile store there are no stored
   preferences, and managed's defaults do not delegate.
4. Power, inside the job under its own admission (`Reader::lighting_power`):
   Windows battery is the battery class with no adapter read. On Windows AC
   one fresh 07/8C reading, the query every thermal recipe reads, classifies
   the adapter, and Windows must still report AC after it. A classified
   adapter admits lighting. An unclassified one needs managed
   `CanAdmitUnclassifiedExternalControl` (UnclassifiedExternalPower.cs 60-84),
   each clause from native's own fresh evidence: fresh Windows AC presence, a
   fresh firmware observation that HyperBoost is not engaged (a failed one
   admits nothing), no unsettled thermal step (`Storage::pending`, which also
   covers managed's `WritesBlocked` and pending indeterminate intents), and,
   with owned HyperBoost, managed's journal absent or Relinquished and no
   native ownership in the history. Windows unknown, an adapter battery
   reading Windows AC contradicts, or a refused clause is Blocked with
   managed's unclassified-adapter text (14935-14943), before anything is
   stored.
5. The class becomes current, with its Windows power generation, as a
   thermal step's store makes it, so a temporary override survives the next
   thermal step on the same power. The intent is stored
   (`SetLightingOverrideCoreAsync`, 14820) in `manual-overrides.json`
   `overrides.lighting`, in managed's format and member order, normalized as
   managed `SaveAsync` normalizes it. Only a persistent override is saved; a
   temporary one clears the saved one and is held in memory for its power
   class. A failed store is managed's rejected intent (`RecordRejectedIntent`,
   20495). Only a quarantined override document refuses the store: no thermal
   journal row or recovery rewrites the lighting member, so an unsettled
   thermal step does not.
6. Apply: read the baseline, write the brightness, read it back, then the logo
   effect (for a lit mode) and state, each read back, then managed's
   aggregate read. A confirmed mismatch restores the baseline. A setter that
   may have been submitted without confirmation, or a final read that cannot
   confirm a written member (managed's indeterminate write with confirmation
   unavailable), stops the request and latches the doubt in step 2. A reply
   other than Applied is prefixed "{committed} Hardware result: ", as managed
   prefixes it.

Managed refreshes competing-controller presence after power resolution and
before the store. Native's fresh admission confirms controller absence before
the job and at each of its checkpoints, the one before the 07/8C read
included, so a controller blocks before anything is stored in both.

A zero brightness confirms Off only after a second zero sample 750 ms later
(managed `OwnedRendererOffObservation`), at the baseline and at the final
read, for every Off write: a keyboard lit again between the samples is
written.

Managed's Driver-mode ensure in `ProductionLightingFactory` builds the Fn
layer and the M1-M5 input reports for its port; it is no prerequisite of the
brightness and logo commands, so slice L1 sends none. The keyboard Driver-mode
owner keeps its own admission.

## Status

The status projects `lightingOverride` and `desiredState.lighting` (source 4
for a persistent override, 3 for a temporary one) from the store, in every
request's status, the thermal and fan requests' included
(`Runtime::project_saved_members`). A request's fresh reading joins the
confirmed state a thermal read established: `confirmedState.lighting` is the
Off record for a zero brightness and null otherwise (managed
`ReadStateCoreAsync`, ProductionLightingPort.cs 186), and
`confirmedState.logoLighting` is the managed wire value. A member whose setter
may have been submitted, or whose last read was outside the contract, is null
until a read confirms it. The native status carries no `unconfirmedWrites`
member, so the doubt is published only through those nulls and the pending
refusal.

Lighting records are written with `kind` first in every object
(`openblade_domain::kind_first`): managed binds `LightingParameters` with a
polymorphic `kind` discriminator and refuses it out of place. The status
records (`FeatureRecord`) and the thermal convergence record use it.

## Interim divergences

Each is named by a test and removed by slice L2:

- Capabilities admit the keyboard Off only, with every logo mode. A lit
  effect is stored and then refused as managed refuses an effect its
  capability table lacks
  (`lit_effects_are_refused_until_l2_interim_divergence`).
- Clearing the override stores the clear and applies no profile lighting.
  Managed applies the profile's lighting there (ControlCoordinator.cs
  `ApplyLightingOutcomeAsync`)
  (`lit_effects_are_refused_until_l2_interim_divergence`).
- Power-source transitions (`ApplyFullTransitionAsync`, ServiceControlRuntime.cs
  14948-14956) and profile reconciliation run no lighting step, and a
  restart writes no lighting
  (`a_restart_keeps_the_saved_intent_and_writes_nothing`). So a request
  stored while the low-power gate is set is not applied after standby, and
  its reply is the committed message, Stored, instead of managed's "Lighting
  intent was stored and will be applied after standby."
  (`the_low_power_store_promises_no_resume_apply_interim_divergence`).
- The periodic thermal observation does not read lighting, so the next
  thermal read publishes the lighting members as null until a lighting
  request reads them again. Managed reads lighting in every observation
  (`a_thermal_read_publishes_no_lighting_members_interim_divergence`).
- `setExternalLightingOverridePolicy` is not served and gets the generic
  unsupported reply
  (`every_absent_managed_operation_gets_the_typed_unsupported_reply`).

## Stricter than managed

- Every Off write confirms a zero brightness with the second sample; managed
  samples twice only while it owns a renderer.
- The saved-override loader refuses a document managed would migrate (an
  older schema, or lighting that managed's visibility normalization changes)
  and preserves its bytes for managed.

## Guards

`lighting_guard_tests.rs` fails when non-test source names a lighting setter
outside its codec, the recipe and the semantic admission, builds a lighting
job outside `lighting_mutation.rs`, changes the class, query or setter IDs the
codec builds, or enables `lighting-setters` from another feature.

## Validation still needed

Attended validation on exact RZ09-0581: Off from a lit keyboard with each
logo mode, readback confirmation, a deliberate restore after a mismatch, and
restoration of the saved baseline afterwards.
