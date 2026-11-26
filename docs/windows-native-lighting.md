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
   is read or written. The reply is Stored with the committed message (see
   the interim divergences). A temporary override needs Windows battery, the
   one class known without an adapter read; otherwise it is Blocked with its
   own text naming the low-power state (see Stricter than managed).
2. A possible earlier submission left unconfirmed: a read-only job runs, the
   reply is managed's pending refusal (`PendingWriteBlockedResult`, 19255),
   and a complete read clears the doubt. Nothing is replayed.
3. Keyboard delegated to Windows Dynamic Lighting in the stored preferences
   (`IsKeyboardLightingDelegated`): Blocked, managed's delegation text, before
   anything is stored. Managed checks it in the apply, after the store, and
   replies with the committed prefix (14965, 15054, 15213); see Stricter than
   managed. Without a profile store there are no stored preferences, and
   managed's defaults do not delegate.
4. Power, inside the job under its own admission (`Reader::lighting_power`):
   Windows battery is the battery class with no adapter read. On Windows AC
   one fresh 07/8C reading, the query every thermal recipe reads, classifies
   the adapter, and Windows must still report AC after it. A classified
   adapter admits lighting. An unclassified one needs managed
   `CanAdmitUnclassifiedExternalControl` for Lighting
   (UnclassifiedExternalPower.cs 60-77), each clause from native's own fresh
   evidence: fresh Windows AC presence; no unsettled journal step
   (`Storage::pending`: the store's quarantine or an unsettled last row of the
   one native journal, which holds the thermal, fan and battery rows; native
   keeps no other battery or HyperBoost doubt, so it stands for managed's
   pending indeterminate HyperBoost intents, and it is not managed's
   `WritesBlocked` status flag); and, with owned HyperBoost, managed's journal
   absent or Relinquished and no native ownership in the history. Native also
   requires a fresh firmware observation that HyperBoost is not engaged, and
   a failed one admits nothing; managed's Lighting arm has no such clause (see
   Stricter than managed). Windows unknown, an adapter battery
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
   thermal step does not. A clear is stored and writes nothing: on an
   unclassified adapter the reply is managed's Stored "Override cleared.
   Power-profile application waits for adapter classification." (15012); on a
   classified one see the interim divergences.
6. Apply: read the baseline, write the brightness, read it back, then the logo
   effect (for a lit mode) and state, each read back, then managed's
   aggregate read. A confirmed mismatch restores the baseline. A setter that
   may have been submitted without confirmation, or a final read that cannot
   confirm a written member (managed's indeterminate write with confirmation
   unavailable), stops the request and latches the doubt in step 2. A reply
   other than Applied is prefixed "{committed} Hardware result: ", as managed
   prefixes it.

Native's fresh admission confirms competing-controller absence before the job
(`capture_intent`) and at each of its checkpoints, the one before the 07/8C
read included, so a controller blocks before anything is stored. Managed's
refresh after power resolution
(`RefreshControllerPresenceAndRelinquishLightingAsync`) only observes the
controller and relinquishes lighting: it stores at 14965 and refuses the
apply afterwards (see Stricter than managed).

A zero brightness confirms Off only after a second zero sample 750 ms later
(managed `OwnedRendererOffObservation`), at the baseline and at the final
read, for every Off write: a keyboard lit again between the samples is
written. The read-only resolution samples a zero twice too, and a single zero
sample, such as a brightness setter's readback, confirms nothing, so status
publishes the Off record only from a double-sampled read
(`a_single_zero_brightness_sample_publishes_no_confirmed_off`).

Managed's lighting port always enters Driver mode before it writes
(`EnsureDriverModeAsync`, ProductionLightingFactory.cs 226-242), so managed
writes these setters in Driver mode. Slice L1 sends no Driver-mode command:
the reviewed brightness and logo commands name no mode prerequisite, and the
keyboard Driver-mode owner keeps its own admission. A Service without
`service-keyboard-driver-mode-v1` therefore normally writes them in Normal
mode. That is source-backed inference, not a physical observation; the
attended validation below covers it.

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
- Clearing the override on a classified adapter stores the clear, applies no
  profile lighting and replies Stored, "Lighting override was cleared. The
  profile's lighting was not applied." Managed applies the profile's lighting
  there (ControlCoordinator.cs `ApplyLightingOutcomeAsync`)
  (`a_clear_is_stored_and_says_what_waits`).
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

Each of these is refused before anything is stored; managed stores it:

- A keyboard delegated to Windows Dynamic Lighting. Managed stores the intent
  and replies "Lighting override intent was stored. Hardware result: ..."
  with its delegation text (ServiceControlRuntime.cs 14965, 15054, 15213)
  (`a_keyboard_delegated_to_dynamic_lighting_blocks_before_any_store`).
- A competing controller. Managed's refresh only observes the controller and
  relinquishes lighting, so it stores and refuses the apply afterwards
  (`refused_admission_stores_and_sends_nothing`).
- An unclassified adapter while firmware reports HyperBoost engaged, or when
  that fresh observation fails. Managed's Lighting arm has no
  `HyperBoostActive` clause (UnclassifiedExternalPower.cs 60-77)
  (`an_unclassified_adapter_admits_lighting_only_without_hyperboost_or_a_pending_step`).
- A temporary override while the low-power gate is set, off Windows battery.
  Managed stores it with no power class (14903-14918); native's refusal names
  the low-power state
  (`a_low_power_temporary_override_needs_windows_battery_divergence`).

Also stricter:

- Every Off write, and the read-only resolution, confirms a zero brightness
  with the second sample; managed samples twice only while it owns a
  renderer.
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
restoration of the saved baseline afterwards. Record the keyboard device mode
(Normal or Driver) of each run. Managed writes these setters in Driver mode;
a Service without `service-keyboard-driver-mode-v1` is expected to write them
in Normal mode, which no physical run has observed. When the keyboard feature
is composed, run the checks in both modes.

## Follow-ups

- The native fixture-manifest corpus for lighting must land before lighting
  joins a packaged tuple or slice L2 makes the Tray path reachable.
